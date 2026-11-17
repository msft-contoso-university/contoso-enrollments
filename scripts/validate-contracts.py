import json
import sys
import yaml
from jsonschema import RefResolver
from openapi_schema_validator import OAS30Validator, OAS31Validator
from openapi_spec_validator import validate

with open("contracts/enrollments.openapi.yaml", encoding="utf-8-sig") as f:
    enrollments = yaml.safe_load(f)
with open(sys.argv[1], encoding="utf-8-sig") as f:
    students = yaml.safe_load(f)
with open(sys.argv[2], encoding="utf-8-sig") as f:
    academics = yaml.safe_load(f)
for spec in (enrollments, students, academics):
    validate(spec)
student_lookup = students["paths"]["/api/v1/students/{id}"]["get"]
course_lookup = academics["paths"]["/api/v1/courses/{id}"]["get"]
for lookup in (student_lookup, course_lookup):
    assert set(("200", "401", "403", "404")) <= set(lookup["responses"])
assert "Students.Lookup" in student_lookup["description"] and "idtyp=app" in student_lookup["description"]
assert "Academics.Lookup" in course_lookup["description"] and "idtyp=app" in course_lookup["description"]
for spec, lookup, validator, fixture in (
    (students, student_lookup, OAS30Validator, "student.json"),
    (academics, course_lookup, OAS31Validator, "course.json"),
):
    with open("tests/Contoso.Enrollments.Tests/Fixtures/" + fixture, encoding="utf-8-sig") as f:
        body = json.load(f)
    schema = lookup["responses"]["200"]["content"]["application/json"]["schema"]
    validator(schema, resolver=RefResolver.from_schema(spec)).validate(body)
assert set(enrollments["paths"]["/api/v1/enrollments"]) == {"get", "post"}
assert set(enrollments["paths"]["/api/v1/enrollments/{id}"]) == {"parameters", "get", "put", "delete"}
assert enrollments["components"]["schemas"]["Grade"]["enum"] == ["A", "B", "C", "D", "F", None]
print("Validated enrollment contract and real HTTP fixture shapes against pinned upstream contracts")
