"""Explicit new-instance database provisioner; never part of ordinary Web startup."""
from src.portable.provision import main

if __name__ == "__main__":
    raise SystemExit(main())
