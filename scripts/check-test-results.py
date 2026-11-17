import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
results = [e for e in root.iter() if e.tag.endswith("UnitTestResult")]
if not any(r.get("testName") == "MigrationsUniqueIndexAndConcurrencyUseRealSqlServer" and r.get("outcome") == "Passed" for r in results):
    raise SystemExit("The real SQL Server test did not pass")
if any(r.get("outcome") != "Passed" for r in results):
    raise SystemExit("Skipped or failed test found")
