"""锁定已发行 v1 指纹，避免后续 ORM 新表污染旧版初始化和恢复。"""

import unittest

from src.portable.schema import _application_table_names, _portable_metadata
from src.portable.schema_catalog import (
    V1_FINGERPRINT, V1_INDEXES, V1_TABLE_COLUMNS, V1_TABLES, V2_BUSINESS_TABLES, V2_FINGERPRINT, V2_TABLES,
    expected_tables,
)
from src.storage.models import Base
from src.portable.upstream_ddl import (
    CREATE_STATEMENTS, MIGRATION_CHECKSUM, MIGRATION_ID, V2_INDEXES, ddl_checksum,
)


class PortableSchemaCatalogTests(unittest.TestCase):
    def test_v1_snapshot_matches_existing_orm(self):
        self.assertTrue(set(V1_TABLE_COLUMNS).issubset(Base.metadata.tables))
        self.assertEqual(set(_application_table_names()), set(V1_TABLE_COLUMNS))
        for name, columns in V1_TABLE_COLUMNS.items():
            self.assertEqual(tuple(column.name for column in Base.metadata.tables[name].columns), columns, name)
        portable_metadata = _portable_metadata()
        actual_indexes = {index.name for table in portable_metadata.tables.values()
                          for index in table.indexes}
        self.assertEqual(actual_indexes, V1_INDEXES)
        self.assertEqual(set(portable_metadata.tables), {"public." + name for name in V1_TABLE_COLUMNS})

    def test_versions_have_distinct_exact_table_sets(self):
        self.assertEqual(expected_tables(1), V1_TABLES)
        self.assertEqual(expected_tables(2), V2_TABLES)
        self.assertEqual(V2_TABLES - V1_TABLES, V2_BUSINESS_TABLES | {"app_schema_migrations"})
        with self.assertRaises(ValueError):
            expected_tables(3)
        self.assertEqual(len(V1_FINGERPRINT), 64)
        self.assertEqual(len(V2_FINGERPRINT), 64)
        self.assertNotEqual(V1_FINGERPRINT, V2_FINGERPRINT)

    def test_v2_ddl_is_fixed_and_does_not_cascade_task_deletion(self):
        self.assertEqual(MIGRATION_ID, "002_upstream_features")
        self.assertEqual(ddl_checksum(), MIGRATION_CHECKSUM)
        ddl = "\n".join(CREATE_STATEMENTS)
        self.assertEqual(len(CREATE_STATEMENTS), 10)
        for table in V2_BUSINESS_TABLES | {"app_schema_migrations"}:
            self.assertIn("CREATE TABLE public." + table, ddl)
        for index in V2_INDEXES:
            self.assertIn("INDEX " + index, ddl)
        self.assertNotIn("REFERENCES public.tasks", ddl)
        self.assertNotIn("REFERENCES public.monitoring_results", ddl)


if __name__ == "__main__":
    unittest.main()
