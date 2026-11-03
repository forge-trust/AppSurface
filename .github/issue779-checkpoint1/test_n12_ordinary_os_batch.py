"""N12 runs the same detached OS-cache controls; no root/native authority."""
from unittest.mock import patch
import test_none_ordinary_os_batch as controls


class N12OrdinaryOsBatchTests(controls.NoneOrdinaryOsBatchTests):
    def setUp(self):
        selected = controls.ROOT / "procedures/n12/checkpoint-n12-data-integration.sh"
        override = patch.object(controls, "SCRIPT", selected)
        override.start()
        self.addCleanup(override.stop)
