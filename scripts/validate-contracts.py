import sys
import yaml
from openapi_spec_validator import validate

with open("contracts/enrollments.openapi.yaml", encoding="utf-8-sig") as f:
    enrollments = yaml.safe_load(f)
with open(sys.argv[1], encoding="utf-8-sig") as f:
    students = yaml.safe_load(f)
validate(enrollments)
validate(students)
lookup = students["paths"]["/api/v1/students/{id}"]["get"]
assert set(("200", "401", "403", "404")) <= set(lookup["responses"])
assert "Students.Lookup" in lookup["description"] and "idtyp=app" in lookup["description"]
student = students["components"]["schemas"]["Student"]
assert student["properties"]["id"]["type"] == "integer"
assert set(enrollments["paths"]["/api/v1/enrollments"]) == {"get", "post"}
assert set(enrollments["paths"]["/api/v1/enrollments/{id}"]) == {"parameters", "get", "put", "delete"}
assert enrollments["components"]["schemas"]["Grade"]["enum"] == ["A", "B", "C", "D", "F", None]
print("Validated enrollment contract and pinned Students lookup interface")
