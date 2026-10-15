"""Build-time PE import inspection; no Python dependency in the distributed app."""
import json
import pathlib
import struct
import sys


def imports(path):
    data = path.read_bytes()
    if data[:2] != b'MZ':
        return []
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    count, opt_size = struct.unpack_from('<H', data, pe + 6)[0], struct.unpack_from('<H', data, pe + 20)[0]
    opt = pe + 24
    is64 = struct.unpack_from('<H', data, opt)[0] == 0x20b
    data_dirs = opt + (112 if is64 else 96)
    section_table = opt + opt_size
    sections = [struct.unpack_from('<IIII', data, section_table + i * 40 + 8) for i in range(count)]

    def offset(rva):
        for virtual_size, virtual_address, raw_size, raw_pointer in sections:
            if virtual_address <= rva < virtual_address + max(virtual_size, raw_size):
                return raw_pointer + rva - virtual_address
        return rva

    names = []
    for index, stride, name_offset in [(1, 20, 12), (13, 32, 4)]:
        rva, size = struct.unpack_from('<II', data, data_dirs + index * 8)
        if not rva:
            continue
        table = offset(rva)
        for pos in range(table, min(table + size, len(data)), stride):
            if not any(data[pos:pos + stride]):
                break
            name_rva = struct.unpack_from('<I', data, pos + name_offset)[0]
            start = offset(name_rva)
            end = data.index(b'\0', start)
            names.append(data[start:end].decode('ascii', errors='replace').lower())
    return names


root = pathlib.Path(sys.argv[1]).resolve()
files = [p for p in root.rglob('*') if p.suffix.lower() in ('.dll', '.exe')]
shipped = {p.name.lower() for p in files}
crt = {}
for path in files:
    for dep in imports(path):
        if dep.startswith(('msvcp140', 'vcruntime140', 'concrt140')) and dep not in shipped:
            crt.setdefault(dep, []).append(str(path.relative_to(root)))
print(json.dumps({'files_inspected': len(files), 'missing_crt_imports': crt}, ensure_ascii=False, indent=2))
sys.exit(1 if crt else 0)
