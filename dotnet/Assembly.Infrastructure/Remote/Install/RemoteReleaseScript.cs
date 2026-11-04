namespace Assembly.Infrastructure.Remote.Install;

/// <summary>Runs on the remote: downloads the archive straight from GitHub, checks it against the signed record and unpacks it.</summary>
internal static class RemoteReleaseScript
{
    internal const string Download = """
import hashlib, json, os, sys, tarfile, time, urllib.request
url, archive_path, total_text, trusted_hash, expected_version = sys.argv[1:]
total = int(total_text)
payload_files = {'install.sh', 'payload/assembly-remote-server', 'payload/dotnet/scripts/agents-file.sh', 'payload/scripts/package-adapters.ts'}
expected_files = payload_files | {'manifest.json', 'SHA256SUMS'}
expected_directories = {'', '.', 'payload', 'payload/dotnet', 'payload/dotnet/scripts', 'payload/scripts'}
request = urllib.request.Request(url, headers={'User-Agent': 'Assembly-backend-installer'})
with urllib.request.urlopen(request, timeout=30) as response:
    if not response.geturl().startswith('https://'):
        raise ValueError('Backend release redirected outside HTTPS')
    declared = response.headers.get('Content-Length')
    if declared and int(declared) != total:
        raise ValueError('Backend release size differs from signed record')
    digest = hashlib.sha256()
    written, last_report = 0, time.monotonic()
    with open(archive_path, 'xb') as output:
        os.chmod(archive_path, 0o600)
        while chunk := response.read(65536):
            written += len(chunk)
            if written > total:
                raise ValueError('Backend release exceeds signed size')
            digest.update(chunk)
            output.write(chunk)
            now = time.monotonic()
            if now - last_report >= .25:
                print(f'P\t{written}', flush=True)
                last_report = now
        output.flush()
    print(f'P\t{written}', flush=True)
if written != total or digest.hexdigest() != trusted_hash:
    raise ValueError('Backend release is truncated or differs from signed digest')
stage = os.path.dirname(archive_path)
found = set()
with tarfile.open(archive_path, 'r:gz') as archive:
    for member in archive:
        name = member.name.removeprefix('./').rstrip('/')
        if member.isdir() and name in expected_directories:
            continue
        if not member.isfile() or name not in expected_files or name in found:
            raise ValueError(f'Unexpected backend archive entry: {name}')
        found.add(name)
        destination = os.path.join(stage, name)
        os.makedirs(os.path.dirname(destination), exist_ok=True)
        source = archive.extractfile(member)
        if source is None:
            raise ValueError(f'Cannot read backend archive entry: {name}')
        with open(destination, 'xb') as output:
            while chunk := source.read(65536):
                output.write(chunk)
    if found != expected_files:
        raise ValueError('Backend archive is missing required entries')
with open(os.path.join(stage, 'manifest.json'), encoding='utf-8') as source:
    manifest = json.load(source)
if (manifest.get('schemaVersion') != 1 or manifest.get('packageType') != 'assembly-remote-backend'
        or manifest.get('target') != 'x86_64-unknown-linux-gnu'
        or manifest.get('version') != expected_version):
    raise ValueError('Backend manifest does not match signed release')
files = manifest.get('files')
if not isinstance(files, list) or len(files) != len(payload_files):
    raise ValueError('Backend manifest file list is invalid')
listed = set()
for item in files:
    if not isinstance(item, dict) or item.get('path') not in payload_files:
        raise ValueError('Backend manifest file list is invalid')
    path, expected = item['path'], item.get('sha256')
    if path in listed or not isinstance(expected, str) or len(expected) != 64:
        raise ValueError('Backend manifest checksum is invalid')
    listed.add(path)
    actual = hashlib.sha256()
    with open(os.path.join(stage, path), 'rb') as source:
        while chunk := source.read(65536):
            actual.update(chunk)
    if actual.hexdigest() != expected:
        raise ValueError(f'Backend payload checksum failed: {path}')
if listed != payload_files:
    raise ValueError('Backend manifest is missing payloads')
commit = manifest.get('commit', '')
if not isinstance(commit, str) or len(commit) < 12 or len(commit) > 40 or any(c not in '0123456789abcdef' for c in commit):
    raise ValueError('Backend manifest commit is invalid')
server_hash = next(item['sha256'] for item in files if item['path'] == 'payload/assembly-remote-server')
print(f'V\t{expected_version}\t{commit}\t{server_hash}', flush=True)
""";
}
