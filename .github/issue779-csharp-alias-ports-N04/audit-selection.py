"""Static byte-selection audit; never imports or executes fixture code."""
import hashlib,json,pathlib,re
ROOT=pathlib.Path(__file__).resolve().parent
m=json.loads((ROOT/'selection.json').read_text())
for kind,item in m['selections'].items():
 full=(ROOT/(kind+'-full-source.data')).read_bytes()
 assert hashlib.sha256(full).hexdigest()==item['full_sha256']
 lines=full.splitlines(keepends=True); selected=[]
 for entry in item['functions']:
  block=b''.join(lines[entry['first_line']-1:entry['last_line']])
  assert len(block)==entry['bytes'] and hashlib.sha256(block).hexdigest()==entry['sha256']
  assert re.match(rb'^'+entry['name'].encode()+rb'\(\) \{',block)
  selected.append(block)
 actual=(ROOT/item['selected_file']).read_bytes()
 assert actual==b'\n'.join(selected)
 assert hashlib.sha256(actual).hexdigest()==item['selected_sha256']
 assert b'negative_os_cache_initialize()' in actual and b'verify_os_path()' in actual
print('EXACT_ALIAS_FUNCTION_SELECTION_OK',flush=True)
