import json
import sys

with open(sys.argv[1], encoding="utf-8-sig") as f:
    report = json.load(f)
if report.get("problems"):
    raise SystemExit("NuGet audit reported errors: " + str(report["problems"]))
for project in report.get("projects", []):
    for framework in project.get("frameworks", []):
        for group in ("topLevelPackages", "transitivePackages"):
            for package in framework.get(group, []):
                if package.get("vulnerabilities"):
                    raise SystemExit("Vulnerable package: " + package["id"])
print("NuGet audit: no findings")
