#!/usr/bin/env python3
"""Archive fixtures for portable release validation; no release downloads required."""

import hashlib
import io
import json
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

from validate_release_assets import (
    EXPECTED_ASSETS,
    OMASNAP_REQUIRED_FILES,
    build_file_name,
    ensure_optional_omasnap,
    ensure_portable_zip_payload,
    expected_assets,
    main,
)


class PortableReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.path = Path(self.temp_dir.name) / "portable.zip"
        self.payload = {
            "XerahS.exe": b"app",
            "xerahs-watchfolder-daemon.exe": b"daemon",
            "portable.txt": b"",
            "coreclr.dll": b"runtime",
            "LICENSE.txt": b"license",
            "Plugins/example/plugin.json": json.dumps({"assemblyFileName": "Example.dll"}).encode(),
            "Plugins/example/Example.dll": b"plugin",
        }

    def write_archive(self):
        with zipfile.ZipFile(self.path, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, content in self.payload.items():
                archive.writestr(name, content)

    def test_both_architectures_are_required_with_matching_names(self):
        for arch in ("x64", "arm64"):
            self.assertIn(("win", arch, "portable.zip"), EXPECTED_ASSETS)
            self.assertEqual(
                build_file_name("1.2.3", "win", arch, "portable.zip"),
                f"XerahS-1.2.3-win-{arch}-portable.zip",
            )
        self.assertEqual(build_file_name("1.2.3", "win", "x64", "exe"), "XerahS-1.2.3-win-x64.exe")

    def test_valid_archive_allows_empty_marker(self):
        self.write_archive()
        ensure_portable_zip_payload(self.path)

    def test_required_payload_cannot_be_missing(self):
        for name in ("XerahS.exe", "portable.txt", "xerahs-watchfolder-daemon.exe"):
            with self.subTest(name=name):
                content = self.payload.pop(name)
                self.write_archive()
                with self.assertRaisesRegex(RuntimeError, "Missing portable payload"):
                    ensure_portable_zip_payload(self.path)
                self.payload[name] = content

    def test_wrapping_directory_is_rejected(self):
        self.payload = {"XerahS/" + name: value for name, value in self.payload.items()}
        self.write_archive()
        with self.assertRaisesRegex(RuntimeError, "Missing portable payload"):
            ensure_portable_zip_payload(self.path)

    def test_declared_plugin_assembly_must_be_present(self):
        del self.payload["Plugins/example/Example.dll"]
        self.write_archive()
        with self.assertRaisesRegex(RuntimeError, "Missing or empty plugin assembly"):
            ensure_portable_zip_payload(self.path)

    def test_plugin_free_archive_is_rejected(self):
        self.payload = {name: value for name, value in self.payload.items() if not name.startswith("Plugins/")}
        self.write_archive()
        with self.assertRaisesRegex(RuntimeError, "No plugin manifests"):
            ensure_portable_zip_payload(self.path)

    def test_empty_executable_is_rejected(self):
        self.payload["XerahS.exe"] = b""
        self.write_archive()
        with self.assertRaisesRegex(RuntimeError, "Empty portable payload"):
            ensure_portable_zip_payload(self.path)

    def test_symbols_are_rejected_even_inside_plugins(self):
        self.payload["Plugins/example/Example.PDB"] = b"symbols"
        self.write_archive()
        with self.assertRaisesRegex(RuntimeError, "Debug symbols"):
            ensure_portable_zip_payload(self.path)

    def test_unsafe_member_paths_are_rejected(self):
        for name in ("../escape.dll", "/absolute.dll", "C:/drive.dll"):
            with self.subTest(name=name):
                self.payload[name] = b"bad"
                self.write_archive()
                with self.assertRaisesRegex(RuntimeError, "Unsafe portable archive path"):
                    ensure_portable_zip_payload(self.path)
                del self.payload[name]


class OptionalOmaSnapTests(unittest.TestCase):
    """OmaSnap is optional in Linux archives (XIP0088): checked only when present."""

    base = {"XerahS", "omaxerahs", "omaxerahs.runtimeconfig.json"}

    def test_archive_without_omasnap_passes(self):
        ensure_optional_omasnap(set(self.base), Path("x.tar.gz"))

    def test_complete_omasnap_bundle_passes(self):
        names = set(self.base) | {"omasnap/"} | set(OMASNAP_REQUIRED_FILES)
        ensure_optional_omasnap(names, Path("x.tar.gz"))

    def test_nested_root_folder_is_supported(self):
        names = {"XerahS-1.0.0/" + name for name in OMASNAP_REQUIRED_FILES}
        ensure_optional_omasnap(names, Path("x.tar.gz"))

    def test_missing_license_fails(self):
        names = set(self.base) | {"omasnap/omasnap", "omasnap/licenses/LICENSE-MIT"}
        with self.assertRaisesRegex(RuntimeError, "LICENSE-OFL"):
            ensure_optional_omasnap(names, Path("x.tar.gz"))

    def test_unrelated_names_containing_omasnap_are_ignored(self):
        ensure_optional_omasnap(set(self.base) | {"docs/notomasnap/readme"}, Path("x.tar.gz"))


class LinuxReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp_dir.cleanup)
        self.root = Path(self.temp_dir.name)
        self.manifest = self.root / 'metadata.json'
        self.version = '1.2.3'

    def write_linux_assets(self):
        payload = {
            'XerahS': b'app',
            'xerahs-watchfolder-daemon': b'daemon',
            'xerahs-watchfolder-daemon.runtimeconfig.json': b'{}',
            'omaxerahs': b'helper',
            'omaxerahs.runtimeconfig.json': b'{}',
        }
        for os_name, arch, extension in expected_assets('linux'):
            path = self.root / build_file_name(self.version, os_name, arch, extension)
            if extension == 'tar.gz':
                with tarfile.open(path, 'w:gz') as archive:
                    for name, data in payload.items():
                        member = tarfile.TarInfo(name)
                        member.size = len(data)
                        archive.addfile(member, io.BytesIO(data))
            else:
                path.write_bytes(b'synthetic package fixture')

    def run_validator(self, platform=None):
        argv = ['validate_release_assets.py', '--assets-dir', str(self.root),
                '--version', self.version, '--metadata-output', str(self.manifest)]
        if platform is not None:
            argv += ['--platform', platform]
        with patch('sys.argv', argv):
            return main()

    def test_linux_requires_both_architectures_and_all_four_formats(self):
        self.assertEqual(len(expected_assets('linux')), 8)
        self.assertEqual({asset[1] for asset in expected_assets('linux')}, {'x64', 'arm64'})
        self.assertEqual({asset[2] for asset in expected_assets('linux')}, {'tar.gz', 'deb', 'rpm', 'AppImage'})
        self.assertEqual(expected_assets('all'), EXPECTED_ASSETS)

    def test_linux_only_release_emits_real_sizes_and_sha256(self):
        self.write_linux_assets()
        self.assertEqual(self.run_validator('linux'), 0)
        metadata = json.loads(self.manifest.read_text(encoding='utf-8'))
        self.assertEqual(metadata['version'], self.version)
        self.assertEqual(len(metadata['assets']), 8)
        for asset in metadata['assets']:
            data = (self.root / asset['file_name']).read_bytes()
            self.assertEqual(asset['os'], 'linux')
            self.assertEqual(asset['size_bytes'], len(data))
            self.assertEqual(asset['sha256'], hashlib.sha256(data).hexdigest())

    def test_missing_arm64_package_blocks_metadata_publication(self):
        self.write_linux_assets()
        (self.root / build_file_name(self.version, 'linux', 'arm64', 'AppImage')).unlink()
        with self.assertRaisesRegex(RuntimeError, 'Missing release asset: .*linux-arm64.AppImage'):
            self.run_validator('linux')
        self.assertFalse(self.manifest.exists())

    def test_default_still_requires_original_all_platform_matrix(self):
        self.write_linux_assets()
        with self.assertRaisesRegex(RuntimeError, 'Missing release asset: .*win-x64.exe'):
            self.run_validator()


if __name__ == "__main__":
    unittest.main()
