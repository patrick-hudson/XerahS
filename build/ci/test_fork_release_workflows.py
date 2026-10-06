"""Exercise the publication policy from the real GitHub Actions condition."""

import ast
from itertools import product
from pathlib import Path
import re
import unittest


def evaluate_condition(expression, contexts):
    expression = expression.replace("&&", " and ").replace("||", " or ")
    expression = re.sub(r"\btrue\b", "True", expression)
    expression = re.sub(r"\bfalse\b", "False", expression)

    def visit(node):
        if isinstance(node, ast.Constant) and isinstance(node.value, (str, bool)):
            return node.value
        if isinstance(node, ast.Name) and node.id in contexts:
            return contexts[node.id]
        if isinstance(node, ast.Attribute):
            parent = visit(node.value)
            if isinstance(parent, dict):
                return parent[node.attr]
        if isinstance(node, ast.BoolOp) and isinstance(node.op, (ast.And, ast.Or)):
            values = [bool(visit(value)) for value in node.values]
            return all(values) if isinstance(node.op, ast.And) else any(values)
        if (isinstance(node, ast.Compare) and len(node.ops) == 1
                and isinstance(node.ops[0], ast.Eq)):
            return visit(node.left) == visit(node.comparators[0])
        raise ValueError(f"Unsupported publication condition: {ast.dump(node)}")

    return bool(visit(ast.parse(expression, mode="eval").body))


class ForkPublicationConditionTests(unittest.TestCase):
    def test_real_publish_condition_honors_manual_preview_and_fork_policy(self):
        workflow = Path(__file__).resolve().parents[2] / ".github/workflows/release-linux.yml"
        text = workflow.read_text(encoding="utf-8")
        match = re.search(r"(?m)^  publish:\n    if: (.+)$", text)
        self.assertIsNotNone(match, "Release workflow must explicitly gate its publication job")
        expression = match.group(1)
        combinations = product(
            ["patrick-hudson/XerahS", "ShareX/XerahS"],
            ["push", "workflow_dispatch", "pull_request", "workflow_call"],
            ["branch", "tag"],
            [False, True],
        )
        for repository, event, ref_type, publish in combinations:
            contexts = {
                "github": {"repository": repository, "event_name": event, "ref_type": ref_type},
                "inputs": {"publish": publish},
            }
            expected = repository == "patrick-hudson/XerahS" and (
                (event == "push" and ref_type == "tag")
                or (event == "workflow_dispatch" and publish)
            )
            with self.subTest(repository=repository, event=event, ref_type=ref_type, publish=publish):
                self.assertEqual(evaluate_condition(expression, contexts), expected)

    def test_condition_evaluator_rejects_calls(self):
        with self.assertRaises(ValueError):
            evaluate_condition("dangerous()", {})


if __name__ == "__main__":
    unittest.main()
