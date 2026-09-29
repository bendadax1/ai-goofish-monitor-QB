"""PG17 schema 的只读结构指纹；不读取业务行或凭据。"""

from __future__ import annotations

import hashlib
import json

from sqlalchemy import text
from sqlalchemy.engine import Connection


def public_schema_fingerprint(connection: Connection) -> str:
    """覆盖 public 表、列类型/默认值/非空、全部约束和索引定义。"""

    queries = (
        """SELECT c.relname, c.relkind, c.relrowsecurity, c.relforcerowsecurity
           FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f', 'c')
           ORDER BY c.relname, c.relkind""",
        """SELECT c.relname, a.attname,
                  pg_catalog.format_type(a.atttypid, a.atttypmod), a.attnotnull,
                  pg_catalog.pg_get_expr(d.adbin, d.adrelid)
           FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
           JOIN pg_attribute a ON a.attrelid = c.oid
           LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
           WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
             AND a.attnum > 0 AND NOT a.attisdropped
           ORDER BY c.relname, a.attnum""",
        """SELECT c.relname, con.conname, con.contype,
                  pg_catalog.pg_get_constraintdef(con.oid, true)
           FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid
           JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname = 'public'
           ORDER BY c.relname, con.conname""",
        """SELECT c.relname, i.relname, pg_catalog.pg_get_indexdef(i.oid)
           FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid
           JOIN pg_class c ON c.oid = x.indrelid
           JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname = 'public'
           ORDER BY c.relname, i.relname""",
        """SELECT p.proname, pg_catalog.pg_get_function_identity_arguments(p.oid)
           FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
           WHERE n.nspname = 'public'
           ORDER BY p.proname, p.oid""",
        """SELECT t.typname, t.typtype
           FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
           WHERE n.nspname = 'public' AND t.typrelid = 0 AND t.typelem = 0
             AND t.typtype IN ('b', 'c', 'd', 'e', 'r', 'm')
           ORDER BY t.typname, t.typtype""",
        """SELECT 'collation', c.collname FROM pg_collation c
           JOIN pg_namespace n ON n.oid = c.collnamespace WHERE n.nspname = 'public'
           UNION ALL SELECT 'conversion', c.conname FROM pg_conversion c
           JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname = 'public'
           UNION ALL SELECT 'operator', o.oprname FROM pg_operator o
           JOIN pg_namespace n ON n.oid = o.oprnamespace WHERE n.nspname = 'public'
           UNION ALL SELECT 'operator_class', o.opcname FROM pg_opclass o
           JOIN pg_namespace n ON n.oid = o.opcnamespace WHERE n.nspname = 'public'
           UNION ALL SELECT 'operator_family', o.opfname FROM pg_opfamily o
           JOIN pg_namespace n ON n.oid = o.opfnamespace WHERE n.nspname = 'public'
           UNION ALL SELECT 'text_search_configuration', c.cfgname FROM pg_ts_config c
           JOIN pg_namespace n ON n.oid = c.cfgnamespace WHERE n.nspname = 'public'
           UNION ALL SELECT 'text_search_dictionary', d.dictname FROM pg_ts_dict d
           JOIN pg_namespace n ON n.oid = d.dictnamespace WHERE n.nspname = 'public'
           UNION ALL SELECT 'text_search_parser', p.prsname FROM pg_ts_parser p
           JOIN pg_namespace n ON n.oid = p.prsnamespace WHERE n.nspname = 'public'
           UNION ALL SELECT 'text_search_template', t.tmplname FROM pg_ts_template t
           JOIN pg_namespace n ON n.oid = t.tmplnamespace WHERE n.nspname = 'public'
           ORDER BY 1, 2""",
        """SELECT c.relname, t.tgname, pg_catalog.pg_get_triggerdef(t.oid)
           FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
           JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname = 'public' AND NOT t.tgisinternal
           ORDER BY c.relname, t.tgname""",
        """SELECT c.relname, p.polname, p.polcmd, p.polpermissive,
                  pg_catalog.pg_get_expr(p.polqual, p.polrelid),
                  pg_catalog.pg_get_expr(p.polwithcheck, p.polrelid)
           FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
           JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname = 'public'
           ORDER BY c.relname, p.polname""",
    )
    sections = []
    for query in queries:
        sections.append([list(row) for row in connection.execute(text(query)).fetchall()])
    payload = json.dumps(sections, ensure_ascii=False, separators=(",", ":"))
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()
