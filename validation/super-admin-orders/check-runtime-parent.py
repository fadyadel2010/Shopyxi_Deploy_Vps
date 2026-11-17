"""Reuse the reviewed Finance assembly-reference gate without editing its evidence."""
import importlib.util
import os
from pathlib import Path

source = Path.cwd()
root = Path("/mnt/c/Users/fadya/AppData/Local/Temp/opencode/orders-release-20261117")
os.environ["FINANCE_RELEASE_ROOT"] = str(root)
spec = importlib.util.spec_from_file_location(
    "orders_parent_gate", source / "validation/super-admin-finance/check-runtime-parent.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
module.RESULT_PATH = source / "validation/super-admin-orders/runtime-parent-compatibility.json"
module.main()
