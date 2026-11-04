namespace Assembly.Infrastructure.Remote.Install;

// Fixed task-owned staging only. Package contents are never executed by this script.
internal static class RemoteInstallScript
{
    internal const string Python = """
import hashlib, json, os, pathlib, pwd, re, shlex, shutil, signal, sqlite3, stat, subprocess, sys, tarfile, uuid
ROOT = pathlib.Path('/home/blackcolours/assembly-dotnet-tsk-1649-engine-integration-fresh')
UNIT = 'assembly-dotnet-tsk-1649-engine-integration-fresh.service'
ENGINE = '872c2b61014142e2aea983c56e1f1047'
MAX_ARCHIVE = 512 * 1024 * 1024
MAX_UNPACKED = 1024 * 1024 * 1024

def require(value):
    if not value:
        raise ValueError('invalid')

def owned(path, directory=False):
    value = path.lstat()
    require(not stat.S_ISLNK(value.st_mode) and value.st_uid == os.getuid())
    require(stat.S_ISDIR(value.st_mode) if directory else stat.S_ISREG(value.st_mode))
    return value

def identity(expected, active=True):
    account = pwd.getpwuid(os.getuid())
    require(os.getuid() == 1000 and account.pw_name == 'blackcolours' and account.pw_dir == '/home/blackcolours')
    require(expected == ENGINE and ROOT.resolve(strict=True) == ROOT)
    owned(ROOT, True)
    reservation = ROOT / 'lane-number'
    owned(reservation)
    require(reservation.read_text().strip() == '9')
    db = ROOT / 'serve' / 'assembly.db'
    require(db.resolve(strict=True) == db)
    owned(db)
    with sqlite3.connect(db.as_uri() + '?mode=ro', uri=True) as connection:
        connection.execute('PRAGMA query_only=ON')
        row = connection.execute('SELECT machine_id FROM engine_meta WHERE singleton=1').fetchone()
        require(row == (ENGINE,))
    if not active:
        return None
    values = subprocess.run(['systemctl', '--user', 'show', UNIT, '--property=FragmentPath,MainPID,ActiveState'],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=10, text=True).stdout
    properties = dict(line.split('=', 1) for line in values.splitlines() if '=' in line)
    require(properties.get('ActiveState') == 'active')
    pid = int(properties.get('MainPID', '0'))
    require(pid > 0)
    unit = pathlib.Path(properties.get('FragmentPath', ''))
    require(unit == pathlib.Path('/home/blackcolours/.config/systemd/user') / UNIT and unit.resolve(strict=True) == unit)
    owned(unit)
    unit_bytes = unit.read_bytes()
    require(len(unit_bytes) <= 65536)
    command = (pathlib.Path('/proc') / str(pid) / 'cmdline').read_bytes().split(b'\0')
    arguments = [part.decode('utf-8', 'strict') for part in command if part]
    require(len(arguments) >= 2 and arguments[0] == '/home/blackcolours/.dotnet/dotnet')
    dll = pathlib.Path(arguments[1])
    require(re.fullmatch(re.escape(str(ROOT)) + r'/bin/[0-9a-f]{40}(?:-[0-9a-f]{64})?/backend/Assembly\.Remote\.dll', str(dll)) is not None)
    require(dll.resolve(strict=True) == dll)
    owned(dll)
    expected_arguments = ['/home/blackcolours/.dotnet/dotnet', str(dll), 'serve', '--port', '9887', '--home', str(ROOT / 'serve'), '--agents', str(ROOT / 'serve' / 'agents.json'), '--token-file', str(ROOT / 'serve' / 'env')]
    require(arguments == expected_arguments)
    starts = [line.split('=', 1)[1] for line in unit_bytes.decode('utf-8', 'strict').splitlines() if line.startswith('ExecStart=')]
    require(len(starts) == 1 and shlex.split(starts[0]) == expected_arguments)
    require(os.readlink('/proc/' + str(pid) + '/cwd') == str(ROOT / 'serve'))
    status = (pathlib.Path('/proc') / str(pid) / 'status').read_text()
    require(re.search(r'^Umask:\s+0077$', status, re.MULTILINE) is not None)
    require(os.readlink('/proc/' + str(pid) + '/exe') == '/home/blackcolours/.dotnet/dotnet')
    return hashlib.sha256(unit_bytes).hexdigest(), pid

def cleanup(stage, operation):
    if not stage.exists() and not stage.is_symlink():
        return
    require(stage.parent == ROOT and stage.name == '.deploy-' + operation and stage.resolve(strict=True) == stage)
    owned(stage, True)
    marker = stage / '.owner'
    owned(marker)
    require(marker.read_text() == operation)
    shutil.rmtree(stage)

def inspect_package(archive):
    members = []
    names = set()
    total = 0
    for member in archive:
        require(len(members) < 4096)
        members.append(member)
        name = member.name
        require(member.isfile() and not member.issym() and not member.islnk() and not member.pax_headers)
        require(re.fullmatch(r'[A-Za-z0-9_.+/-]+', name) is not None and len(name) <= 248)
        require(all(part not in ('', '.', '..') for part in name.split('/')) and not name.startswith('/'))
        require(name not in names and member.mode in (0o644, 0o755) and member.size >= 0)
        names.add(name)
        total += member.size
        require(total <= MAX_UNPACKED)
    require(len(members) > 0 and 'manifest.json' in names)
    manifest_entry = archive.getmember('manifest.json')
    require(manifest_entry.size <= 2 * 1024 * 1024 and manifest_entry.mode == 0o644)
    def unique(items):
        result = {}
        for key, value in items:
            require(key not in result)
            result[key] = value
        return result
    manifest = json.loads(archive.extractfile(manifest_entry).read(), object_pairs_hook=unique)
    require(set(manifest) == {'schemaVersion', 'packageType', 'buildKind', 'target', 'commit', 'protocol', 'format', 'frameworks', 'files'})
    require(type(manifest['schemaVersion']) is int and manifest['schemaVersion'] == 1)
    require(manifest['packageType'] == 'assembly-dotnet-remote-backend-dev' and manifest['buildKind'] == 'development')
    require(manifest['target'] == 'linux-x64' and re.fullmatch(r'[0-9a-f]{40}', manifest['commit']) is not None)
    require(type(manifest['protocol']) is int and manifest['protocol'] == 1 and type(manifest['format']) is int and manifest['format'] == 5)
    files = manifest['files']
    require(isinstance(files, list) and len(files) == len(members) - 1)
    listed = set()
    for item in files:
        require(set(item) == {'path', 'bytes', 'sha256', 'mode'})
        name = item['path']
        require(isinstance(name, str) and name.startswith('backend/') and name in names and name not in listed)
        require(type(item['bytes']) is int and item['bytes'] >= 0 and item['mode'] in ('0644', '0755'))
        require(re.fullmatch(r'[0-9a-f]{64}', item['sha256']) is not None)
        member = archive.getmember(name)
        require(member.size == item['bytes'] and member.mode == int(item['mode'], 8))
        digest = hashlib.sha256()
        stream = archive.extractfile(member)
        while True:
            chunk = stream.read(1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
        require(digest.hexdigest() == item['sha256'])
        listed.add(name)
    require(names == listed | {'manifest.json'})
    require({'backend/Assembly.Remote.dll', 'backend/Assembly.Remote.deps.json', 'backend/Assembly.Remote.runtimeconfig.json'} <= listed)
    runtime_member = archive.getmember('backend/Assembly.Remote.runtimeconfig.json')
    require(runtime_member.size <= 1024 * 1024)
    runtime = json.loads(archive.extractfile(runtime_member).read(), object_pairs_hook=unique)
    frameworks = runtime.get('runtimeOptions', {}).get('frameworks')
    require(isinstance(frameworks, list) and len(frameworks) > 0 and frameworks == manifest['frameworks'])
    for framework in frameworks:
        require(set(framework) == {'name', 'version'} and re.fullmatch(r'[A-Za-z0-9.]+', framework['name']) is not None)
        require(re.fullmatch(r'\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?', framework['version']) is not None)
    return members

def main():
    require(len(sys.argv) == 6)
    mode, operation, expected_hash, expected_bytes, expected_engine = sys.argv[1:]
    require(mode in ('stage', 'cleanup') and str(uuid.UUID(operation)) == operation)
    require(re.fullmatch(r'[0-9a-f]{64}', expected_hash) is not None)
    count = int(expected_bytes)
    require(str(count) == expected_bytes)
    require(count == 0 if mode == 'cleanup' else 0 < count <= MAX_ARCHIVE)
    existing = identity(expected_engine, mode == 'stage')
    stage = ROOT / ('.deploy-' + operation)
    if mode == 'cleanup':
        cleanup(stage, operation)
        return {'cleaned': True}
    unit_hash, pid = existing
    created = False
    success = False
    try:
        stage.mkdir(mode=0o700)
        created = True
        stage_inode = stage.lstat().st_ino
        with (stage / '.owner').open('x') as marker:
            os.chmod(marker.name, 0o600)
            marker.write(operation)
        package = stage / 'archive.tar.gz'
        digest = hashlib.sha256()
        received = 0
        with package.open('xb') as output:
            os.chmod(package, 0o600)
            while received < count:
                chunk = sys.stdin.buffer.read(min(1024 * 1024, count - received))
                require(bool(chunk))
                received += len(chunk)
                digest.update(chunk)
                output.write(chunk)
            require(not sys.stdin.buffer.read(1))
            output.flush()
            os.fsync(output.fileno())
        require(received == count and digest.hexdigest() == expected_hash)
        with tarfile.open(package, 'r:gz') as archive:
            members = inspect_package(archive)
            package_directory = stage / 'package'
            package_directory.mkdir(mode=0o700)
            for member in members:
                destination = package_directory / member.name
                destination.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                with destination.open('xb') as output:
                    shutil.copyfileobj(archive.extractfile(member), output, 1024 * 1024)
                    output.flush()
                    os.fsync(output.fileno())
                os.chmod(destination, member.mode)
        # Recheck the existing target after bounded receive/validation; staging never changes its unit.
        current_hash, current_pid = identity(expected_engine)
        require(current_hash == unit_hash and current_pid == pid)
        success = True
        return {'staged': True, 'bytes': received, 'unitSha256': unit_hash, 'pid': pid}
    finally:
        if created and not success:
            # Exclusive creation establishes ownership even if writing the marker itself failed.
            require(stage.parent == ROOT and stage.resolve(strict=True) == stage)
            require(owned(stage, True).st_ino == stage_inode)
            shutil.rmtree(stage)

if __name__ == '__main__':
    try:
        signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(InterruptedError()))
        print(json.dumps(main(), separators=(',', ':')))
    except BaseException:
        print('{"error":"remote_install_failed"}')
        sys.exit(1)
""";
}
