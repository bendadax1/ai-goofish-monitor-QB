"""Real application-role session check; called only by the isolated PG harness."""

import os
from pathlib import Path
import sys


def main():
    # -I ignores PYTHONPATH; the harness passes the source location explicitly.
    if len(sys.argv) != 2:
        raise RuntimeError("isolated source root is required")
    source = Path(sys.argv[1]).resolve(strict=True)
    sys.path.insert(0, str(source))
    data = Path(os.environ["GOOFISH_PORTABLE_DATA_ROOT"]).resolve()
    if data.parent != Path.cwd().resolve() or data.name != "session-data":
        raise RuntimeError("unexpected isolated data root")

    from src.storage import get_storage
    from src.portable.sessions import issue_session, read_session, revoke_session
    from src.storage.utils import verify_password

    storage = get_storage()
    try:
        user = storage.get_user_by_username("portable_admin")
        assert user is not None
        user_id = str(user["id"])
        first = issue_session(storage, user_id, 3600)
        second = issue_session(storage, user_id, 3600)
        assert read_session(storage, first)["role"] == "super_admin"
        revoke_session(storage, first)
        assert read_session(storage, first) is None
        assert read_session(storage, second) is not None
        storage.update_user(user_id, {"password": "TestSessionPassword7!"})
        assert read_session(storage, second) is None
        updated = storage.get_user_by_username("portable_admin")
        assert verify_password("TestSessionPassword7!", updated["password_hash"])
        third = issue_session(storage, user_id, 3600)
        storage.update_user(user_id, {"role": "viewer"})
        assert read_session(storage, third)["role"] == "viewer"
        storage.update_user(user_id, {"is_active": False})
        assert read_session(storage, third) is None
        storage.update_user(user_id, {"role": "super_admin", "is_active": True})
        print("PORTABLE_DATABASE_SESSIONS=PASS")
    finally:
        storage.engine.dispose()


if __name__ == "__main__":
    main()
