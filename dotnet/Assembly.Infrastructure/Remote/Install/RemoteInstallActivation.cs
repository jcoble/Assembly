using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Remote.Install;

/// <summary>Explicit developer-requested restart of the captured test lane; not a production maintenance lease.</summary>
internal sealed class RemoteInstallActivation(RemoteInstallProcess process)
{
    internal async Task<RemoteInstallResult> ActivateAsync(RemoteInstallStart request, RemoteInstallBinding binding,
        RemoteInstallPackage.Checked package, RemoteInstallStaged staged, CancellationToken ct)
    {
        RemoteInstallTarget.Require(binding, ct);
        var result = await process.ActivateAsync(request, binding, package, staged, ct);
        binding.RequireProfile(ct); // The planned restart intentionally invalidates the old live socket.
        var state = result.GetProperty("state").GetString();
        if (state != "succeeded") throw new CallFailed("The remote restart could not complete safely.",
            state == "rolled_back" ? "activation_rolled_back" : state == "refused" ? "activation_refused" : "activation_uncertain");
        if (result.GetProperty("commit").GetString() != package.Bundle.Commit || result.GetProperty("engine").GetString() != binding.Engine ||
            result.GetProperty("protocol").GetInt32() != 1 || result.GetProperty("format").GetInt32() != 5)
            throw new CallFailed("The remote runtime could not be verified.", "activation_uncertain");
        return new(package.Bundle.Commit, request.Machine, binding.Engine);
    }

    internal const string Python = """
import os, sys, pathlib, pwd, stat, json, hashlib, subprocess, sqlite3, re, time, signal, uuid
root=pathlib.Path('/home/blackcolours/assembly-dotnet-tsk-1649-engine-integration-fresh')
unit='assembly-dotnet-tsk-1649-engine-integration-fresh.service'
unitpath=pathlib.Path('/home/blackcolours/.config/systemd/user')/unit
op, archive_sha, expected_engine, commit, pid_text, unit_sha=sys.argv[1:]
if str(uuid.UUID(op))!=op or not re.fullmatch('[0-9a-f]{64}',archive_sha) or not re.fullmatch('[0-9a-f]{40}',commit): raise ValueError('invalid_request')
if expected_engine!='872c2b61014142e2aea983c56e1f1047' or not pid_text.isdigit() or int(pid_text)<=0: raise ValueError('invalid_request')
stage=root/('.deploy-'+op)
stopped=False
candidate_started=False
before=None
after=None

def command(*args):
    return subprocess.run(list(args),check=True,capture_output=True,text=True,timeout=15).stdout
def properties():
    raw=command('systemctl','--user','show',unit,'--property=MainPID,ControlGroup,ActiveState,FragmentPath')
    return dict(line.split('=',1) for line in raw.strip().splitlines())
def regular(path):
    info=path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid!=os.getuid() or info.st_nlink!=1: raise ValueError('target_changed')
def digest(path):
    value=hashlib.sha256()
    with path.open('rb') as file:
        while chunk:=file.read(65536): value.update(chunk)
    return value.hexdigest()
def atomic_unit(data):
    tmp=unitpath.parent/('.assembly-install-'+op)
    fd=os.open(tmp,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600)
    try:
        with os.fdopen(fd,'wb') as file: file.write(data); file.flush(); os.fsync(file.fileno())
        os.replace(tmp,unitpath)
        fd=os.open(unitpath.parent,os.O_DIRECTORY|os.O_RDONLY)
        try: os.fsync(fd)
        finally: os.close(fd)
    finally:
        if tmp.exists(): tmp.unlink()
def lost_parent(signum, frame): raise RuntimeError('installer_interrupted')
signal.signal(signal.SIGHUP,lost_parent)
signal.signal(signal.SIGTERM,lost_parent)
health_js=r'''
import { readFileSync } from 'node:fs';
const expected=process.argv[1], engine=process.argv[2];
const token=readFileSync('/home/blackcolours/assembly-dotnet-tsk-1649-engine-integration-fresh/serve/env','utf8').split('\n').find(x=>x.startsWith('ASSEMBLY_TOKEN='))?.slice(15);
const socket=new WebSocket('ws://127.0.0.1:9887/socket');
const timer=setTimeout(()=>process.exit(1),1800);
socket.onerror=()=>process.exit(1);
socket.onopen=()=>socket.send(JSON.stringify({t:'hello',protocol:1,token}));
socket.onmessage=e=>{
 let frame; try{frame=JSON.parse(String(e.data))}catch{process.exit(2)}
 if(frame.t!=='welcome'||frame.machine!=='workbox-test'||frame.protocol!==1||frame.format!==5||frame.engine!==engine||frame.commit!==expected)process.exit(2);
 clearTimeout(timer);socket.close();process.stdout.write(JSON.stringify({commit:frame.commit,engine:frame.engine,protocol:frame.protocol,format:frame.format}),()=>process.exit(0));
};
'''
def health(expected_commit, seconds=20):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        read=subprocess.run(['/home/blackcolours/.local/bin/node','--input-type=module','-e',health_js,expected_commit,expected_engine],capture_output=True,text=True,timeout=3)
        if read.returncode==0:
            result=json.loads(read.stdout)
            if result.get('commit')!=expected_commit or result.get('engine')!=expected_engine or result.get('protocol')!=1 or result.get('format')!=5: raise ValueError('health_mismatch')
            return result
        if read.returncode==2: raise ValueError('health_mismatch')
        time.sleep(.1)
    raise ValueError('health_timeout')

try:
    account=pwd.getpwuid(os.getuid())
    if account.pw_name!='blackcolours' or account.pw_dir!='/home/blackcolours': raise ValueError('target_changed')
    for path in [root,root/'bin',stage,stage/'package']:
        info=path.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid!=os.getuid() or path.resolve()!=path: raise ValueError('target_changed')
    if (root/'lane-number').read_text().strip()!='9' or (stage/'.owner').read_text()!=op: raise ValueError('target_changed')
    regular(stage/'archive.tar.gz')
    if digest(stage/'archive.tar.gz')!=archive_sha: raise ValueError('bundle_changed')
    regular(stage/'package/manifest.json')
    manifest=json.loads((stage/'package/manifest.json').read_text())
    if manifest.get('commit')!=commit or manifest.get('protocol')!=1 or manifest.get('format')!=5: raise ValueError('bundle_changed')
    seen=set()
    for item in manifest['files']:
        relative=item['path']
        if not re.fullmatch(r'backend/[A-Za-z0-9_.+/-]+',relative) or any(p in ('','.','..') for p in relative.split('/')) or relative in seen: raise ValueError('bundle_changed')
        seen.add(relative)
        path=stage/'package'/relative
        regular(path)
        if path.resolve()!=path or path.stat().st_size!=item['bytes'] or digest(path)!=item['sha256']: raise ValueError('bundle_changed')
    with sqlite3.connect('file:'+str(root/'serve/assembly.db')+'?mode=ro',uri=True) as db:
        if db.execute('SELECT machine_id FROM engine_meta WHERE singleton=1').fetchone()!=(expected_engine,): raise ValueError('target_changed')
    regular(unitpath)
    if unitpath.resolve()!=unitpath: raise ValueError('target_changed')
    before=unitpath.read_bytes()
    if hashlib.sha256(before).hexdigest()!=unit_sha: raise ValueError('target_changed')
    state=properties()
    if state.get('MainPID')!=pid_text or state.get('ActiveState')!='active' or state.get('FragmentPath')!=str(unitpath): raise ValueError('target_changed')
    cgroup=state.get('ControlGroup','')
    if not cgroup.startswith('/user.slice/user-1000.slice/') or not cgroup.endswith('/'+unit): raise ValueError('target_changed')
    members=set()
    for file in (pathlib.Path('/sys/fs/cgroup')/cgroup.lstrip('/')).rglob('cgroup.procs'):
        members.update(file.read_text().split())
    if members!={pid_text}: raise ValueError('remote_busy')
    # This observed-empty check is NOT a race-free host maintenance lease; this is an explicit manual test-lane restart.
    text=before.decode()
    prefix=str(root)+r'/bin/([0-9a-f]{40}(?:-[0-9a-f]{64})?)/backend/Assembly.Remote.dll'
    start_line=re.search(r'^ExecStart=/home/blackcolours/\.dotnet/dotnet '+prefix+r' serve --port 9887 --home '+str(root)+r'/serve --agents '+str(root)+r'/serve/agents.json --token-file '+str(root)+r'/serve/env$',text,re.M)
    if not start_line: raise ValueError('target_changed')
    old_commit=start_line.group(1)[:40]
    version=commit+'-'+archive_sha
    destination=root/'bin'/version
    if destination.exists():
        if destination.resolve()!=destination: raise ValueError('version_conflict')
        for item in manifest['files']:
            path=destination/item['path']; regular(path)
            if path.stat().st_size!=item['bytes'] or digest(path)!=item['sha256']: raise ValueError('version_conflict')
    else: os.rename(stage/'package',destination)
    lines=text.splitlines(keepends=True)
    lines=[('Description=Assembly Workbox Test development commit '+commit+'\n') if line.startswith('Description=') else
        ('ExecStart=/home/blackcolours/.dotnet/dotnet '+str(destination)+'/backend/Assembly.Remote.dll serve --port 9887 --home '+str(root)+'/serve --agents '+str(root)+'/serve/agents.json --token-file '+str(root)+'/serve/env\n') if line.startswith('ExecStart=') else line for line in lines]
    after=''.join(lines).encode()
    if unitpath.read_bytes()!=before or properties().get('MainPID')!=pid_text: raise ValueError('target_changed')
    stopped=True
    command('systemctl','--user','stop',unit)
    if pathlib.Path('/proc',pid_text).exists(): raise ValueError('old_process_remaining')
    if unitpath.read_bytes()!=before: raise ValueError('target_changed')
    atomic_unit(after)
    command('systemctl','--user','daemon-reload')
    # From this point the candidate may migrate the DB. Never roll code back automatically after an uncertain start.
    candidate_started=True
    command('systemctl','--user','start',unit)
    ready=health(commit)
    print(json.dumps(dict(state='succeeded',**ready)),flush=True)
except BaseException:
    outcome='uncertain'
    if stopped and not candidate_started and before is not None:
        try:
            if unitpath.read_bytes() not in (before,after): raise ValueError('unit_changed')
            atomic_unit(before)
            command('systemctl','--user','daemon-reload')
            command('systemctl','--user','start',unit)
            health(old_commit)
            outcome='rolled_back'
        except BaseException: pass
    elif not stopped:
        outcome='refused'
    print(json.dumps(dict(state=outcome)),flush=True)
""";
}
