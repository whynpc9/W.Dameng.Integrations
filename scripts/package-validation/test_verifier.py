"""Regression checks for the acceptance policy, independent of .NET and databases."""
import unittest

from verify import ValidationError, check_dependencies, libraries


def baseline():
    versions = {
        "W.DamengProvider": "0.3.1", "Hangfire.Core": "1.8.23", "Newtonsoft.Json": "13.0.3",
        "Microsoft.EntityFrameworkCore": "10.0.12", "Microsoft.EntityFrameworkCore.Relational": "10.0.12",
        "W.EntityFrameworkCore.Dameng": "0.5.0", "W.EntityFrameworkCore.Dameng.WDriver": "0.5.0-preview.1",
        "FlexLabs.EntityFrameworkCore.Upsert": "10.1.0", "TickerQ": "10.1.1",
        "TickerQ.Utilities": "10.1.1", "TickerQ.EntityFrameworkCore": "10.1.1",
        "OpenIddict.EntityFrameworkCore": "6.3.0",
    }
    return {name: (version, {"type": "package"}) for name, version in versions.items()}


class DependencyPolicyTests(unittest.TestCase):
    def test_verified_baseline(self):
        check_dependencies(baseline(), True)

    def test_tickerq_direct_dependencies_cannot_drift(self):
        for name in ("TickerQ", "TickerQ.Utilities", "TickerQ.EntityFrameworkCore"):
            with self.subTest(name=name):
                resolved = baseline()
                resolved[name] = ("10.1.2", {"type": "package"})
                with self.assertRaisesRegex(ValidationError, name):
                    check_dependencies(resolved, True)

    def test_missing_required_dependency_is_not_accepted(self):
        resolved = baseline()
        del resolved["W.EntityFrameworkCore.Dameng.WDriver"]
        with self.assertRaisesRegex(ValidationError, "WDriver"):
            check_dependencies(resolved, True)

    def test_old_or_unvalidated_newtonsoft_is_rejected(self):
        for version in ("11.0.1", "13.0.2", "13.0.3-beta1", "14.0.0"):
            with self.subTest(version=version):
                resolved = baseline()
                resolved["Newtonsoft.Json"] = (version, {"type": "package"})
                with self.assertRaisesRegex(ValidationError, "Newtonsoft"):
                    check_dependencies(resolved, False)

    def test_hangfire_without_ef_graph(self):
        resolved = {name: value for name, value in baseline().items()
                    if name in ("W.DamengProvider", "Hangfire.Core", "Newtonsoft.Json")}
        check_dependencies(resolved, False)

    def test_project_reference_cannot_count_as_package(self):
        with self.assertRaisesRegex(ValidationError, "non-package"):
            libraries({"libraries": {"W.Hangfire.Dameng/0.1.0-preview.1": {"type": "project"}}})

    def test_multiple_versions_cannot_hide_wrong_resolution(self):
        with self.assertRaisesRegex(ValidationError, "Multiple resolved versions"):
            libraries({"libraries": {"Newtonsoft.Json/11.0.1": {"type": "package"},
                                      "Newtonsoft.Json/13.0.3": {"type": "package"}}})


if __name__ == "__main__":
    unittest.main(verbosity=2)
