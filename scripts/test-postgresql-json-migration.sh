#!/usr/bin/env bash
# Exercise the exact PostgreSQL statement carried by the server patch, using
# temporary fixtures only. Run after the image's PostgreSQL entrypoint succeeds.
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_dir="$(cd -- "$script_dir/.." && pwd)"
postgres_container="${POSTGRES_CONTAINER:-postgres-ci}"
postgres_user="${POSTGRES_USER:-jellyfin}"
postgres_database="${POSTGRES_DB:-jellyfin}"
export PGPASSWORD="${POSTGRES_PASSWORD:-jellyfin}"

python3 - "$repo_dir/patches/server/0002-Fix-PostgreSQL-StripEmbeddedLinkedChildren.patch" <<'PY' | docker exec -i -e PGPASSWORD "$postgres_container" psql -X -v ON_ERROR_STOP=1 -U "$postgres_user" -d "$postgres_database"
import pathlib
import sys
import textwrap

patch = pathlib.Path(sys.argv[1]).read_text()
added = "\n".join(line[1:] for line in patch.splitlines() if line.startswith("+") and not line.startswith("+++"))
start = added.index('UPDATE "BaseItems"')
end = added.index('              """', start)
statement = textwrap.dedent(added[start:end]).strip() + ";"
assert "pg_input_is_valid" in statement and "WITH UNIQUE KEYS" in statement

fixtures = [
    ("top_level", r'{"Name":"alpha","LinkedChildren":[1],"ExtraIds":["id"],"SupportsExternalTransfer":true,"nested":{"LinkedChildren":[2]}}', r'{"Name":"alpha","nested":{"LinkedChildren":[2]}}', False),
    ("only_obsolete", r'{"LinkedChildren":[],"ExtraIds":[],"SupportsExternalTransfer":false}', '{}', False),
    ("unicode", '{"LinkedChildren":[],"keep":"ä漢字"}', '{"keep":"ä漢字"}', False),
    ("unchanged_object", r'{ "keep" : 1 }', None, True),
    ("nested_only", r'{ "nested" : {"LinkedChildren":[1]} }', None, True),
    ("array", r'["LinkedChildren",{"ExtraIds":["nested"]}]', None, True),
    ("scalar", r'"LinkedChildren"', None, True),
    ("json_null", 'null', None, True),
    ("sql_null", None, None, True),
    ("malformed", r'{"LinkedChildren":', None, True),
    ("duplicate", r'{"keep":1,"keep":2,"LinkedChildren":[]}', None, True),
    ("nested_duplicate", r'{"LinkedChildren":[],"keep":{"k":1,"k":2}}', None, True),
    ("nul_escape", r'{"LinkedChildren":[],"keep":"\u0000"}', None, True),
    ("invalid_surrogate", r'{"LinkedChildren":[],"keep":"\ud800"}', None, True),
    ("oversized_number", r'{"LinkedChildren":[],"keep":1e1000000}', None, True),
]

def sql_literal(value):
    return 'NULL' if value is None else "'" + value.replace("'", "''") + "'"

print('BEGIN;')
print('CREATE TEMP TABLE "BaseItems" ("Id" text PRIMARY KEY, "Data" text, expected text, preserve boolean);')
for name, value, expected, preserve in fixtures:
    if preserve:
        expected = value
    values = [sql_literal(name), sql_literal(value), sql_literal(expected), 'true' if preserve else 'false']
    print('INSERT INTO "BaseItems" VALUES (' + ','.join(values) + ');')
print(statement)
print('''
DO $test$
DECLARE failures text;
BEGIN
    SELECT string_agg("Id", ', ' ORDER BY "Id") INTO failures
    FROM "BaseItems"
    WHERE CASE WHEN preserve
        THEN "Data" IS DISTINCT FROM expected
        ELSE "Data"::jsonb IS DISTINCT FROM expected::jsonb
    END;
    IF failures IS NOT NULL THEN
        RAISE EXCEPTION 'PostgreSQL JSON migration regression: %', failures;
    END IF;
END
$test$;
CREATE TEMP TABLE expected_after AS SELECT "Id", "Data" FROM "BaseItems";
''')
print(statement)
print('''
DO $test$
BEGIN
    IF EXISTS (
        SELECT 1 FROM "BaseItems" b JOIN expected_after e USING ("Id")
        WHERE b."Data" IS DISTINCT FROM e."Data"
    ) THEN
        RAISE EXCEPTION 'PostgreSQL JSON migration is not idempotent';
    END IF;
    IF (
        SELECT min(id) FROM (VALUES
            ('00000000-0000-0000-0000-000000000002'::uuid),
            ('00000000-0000-0000-0000-000000000001'::uuid),
            (NULL::uuid)
        ) AS ids(id)
    ) IS DISTINCT FROM '00000000-0000-0000-0000-000000000001'::uuid THEN
        RAISE EXCEPTION 'Preserved UUID min aggregate failed';
    END IF;
    IF (
        SELECT max(id) FROM (VALUES
            ('00000000-0000-0000-0000-000000000002'::uuid),
            ('00000000-0000-0000-0000-000000000001'::uuid),
            (NULL::uuid)
        ) AS ids(id)
    ) IS DISTINCT FROM '00000000-0000-0000-0000-000000000002'::uuid THEN
        RAISE EXCEPTION 'Preserved UUID max aggregate failed';
    END IF;
END
$test$;
ROLLBACK;
''')
PY

printf '%s\n' 'PostgreSQL JSON migration and UUID aggregate regressions passed.'
