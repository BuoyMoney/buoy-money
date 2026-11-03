"""Run: python -m unittest scripts/license/test_license_scan.py (from repo root: cd scripts/license first)."""
import unittest

import license_scan as L

POLICY = L.load_policy()


class BucketExprTests(unittest.TestCase):
    def test_nested_deny_is_not_bypassed(self):
        self.assertEqual(L.bucket_expr("GPL-2.0-only AND (MIT OR Apache-2.0)", POLICY), 2)

    def test_or_takes_best(self):
        self.assertEqual(L.bucket_expr("MIT OR GPL-3.0-only", POLICY), 0)

    def test_and_binds_tighter_than_or(self):
        self.assertEqual(L.bucket_expr("GPL-3.0-only OR (MIT AND Apache-2.0)", POLICY), 0)
        self.assertEqual(L.bucket_expr("MIT OR GPL-3.0-only AND Apache-2.0", POLICY), 0)

    def test_malformed_is_unknown(self):
        self.assertEqual(L.bucket_expr("MIT AND (", POLICY), 3)


if __name__ == "__main__":
    unittest.main()
