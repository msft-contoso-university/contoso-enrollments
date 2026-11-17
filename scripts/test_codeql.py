import copy
from pathlib import Path
import unittest
from unittest.mock import patch
from check_codeql import validate_files, validate_report


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.report = {"version": "2.1.0", "runs": [{"tool": {"driver": {"name": "CodeQL"}}, "results": []}]}

    def test_requires_all_languages(self):
        with patch("pathlib.Path.glob", return_value=[]), self.assertRaises(ValueError):
            validate_files("unused")
        with patch("pathlib.Path.glob", return_value=[Path("csharp.sarif")]), self.assertRaises(ValueError):
            validate_files("unused")

    def test_valid_report(self):
        validate_report(self.report, "test")

    def test_rejects_missing_or_unsuccessful_evidence(self):
        variants = []
        for key, value in (("version", "1.0.0"), ("runs", [])):
            report = copy.deepcopy(self.report)
            report[key] = value
            variants.append(report)
        for key, value in (("tool", {"driver": {"name": "Other"}}), ("results", [{}]),
                           ("invocations", [{"executionSuccessful": False}])):
            report = copy.deepcopy(self.report)
            report["runs"][0][key] = value
            variants.append(report)
        for report in variants:
            with self.subTest(report=report), self.assertRaises(ValueError):
                validate_report(report, "test")


if __name__ == "__main__":
    unittest.main()
