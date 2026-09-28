"""Focused first-run authorization/login against a disposable real PostgreSQL.

Reuse the existing owned-process/cleanup harness, without unrelated backup,
restore or schema failure-injection cases. Never use an existing PGDATA.
"""

import sys
from unittest.mock import patch

from tests import portable_schema_pg_smoke as harness


def _web_case(*, port, admin, password, app, probe, raw_secrets, test_root):
    database = harness._database_name()
    app_password, probe_password = harness._create_role_and_database(
        port=port, admin=admin, password=password, app=app, probe=probe,
        database=database, raw_secrets=raw_secrets,
    )
    engine = harness._admin_engine(port=port, database=database, user=admin, password=password)
    try:
        harness.schema.initialize_schema(engine, roles=harness.schema.DatabaseRoles(admin, app, probe))
    finally:
        engine.dispose()
    harness._check_real_web(port, database, app, app_password, probe, probe_password, test_root, raw_secrets)
    print("PORTABLE_SETUP_PG_WEB_FLOW=PASS")


def main():
    try:
        with patch.object(harness, "_check_provision", lambda *_: None), patch.object(harness, "_run_schema_cases", _web_case):
            harness.run_smoke()
    except Exception as error:
        # These harness errors are fixed diagnostic messages, never raw DB exceptions.
        detail = str(error) if isinstance(error, harness.pg.SmokeFailure) else type(error).__name__
        print("PORTABLE_SETUP_PG=FAILED " + detail, file=sys.stderr)
        return 1
    print("PORTABLE_SETUP_PG=PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
