import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtempSync, mkdirSync, writeFileSync, realpathSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { spawnSync } from 'node:child_process';
import { checkDataCommand, DATA_DENY } from './data-guard.ts';
import { fixture } from './test-fixture.ts';

// Every path here lives under a temp HOME, so "the protected folder" is a temp folder.
const home = realpathSync(mkdtempSync(join(tmpdir(), 'data-guard-')));
process.env.HOME = home;
mkdirSync(join(home, 'assembly-dotnet/sub'), { recursive: true });
writeFileSync(join(home, 'assembly-dotnet/x'), 'live\n');
process.on('exit', () => rmSync(home, { recursive: true, force: true }));
const D = '~/assembly-dotnet';
const out = join(home, 'copies');
const allowed = (...commands: string[]) => { for (const c of commands) assert.deepEqual(checkDataCommand(c, home), {}, c); };
const denied = (...commands: string[]) => { for (const c of commands) assert.deepEqual(checkDataCommand(c, home), { deny: DATA_DENY }, c); };

test('deny_reason_text_is_exact', () => {
  assert.equal(DATA_DENY, "~/assembly-dotnet is Jesse's live data. Reads and copies out are allowed; writes are not. Make a test copy elsewhere and work on that.");
});
test('exit_status_is_not_a_glob', () => {
  allowed('cd ~ && echo done $?', 'cd ~ && node x.ts > ~/y/z.json; echo done $?', 'cd ~ && node "$W/x.ts"; echo $?');
  denied('cd ~; echo $?; rm -rf assembly*', 'echo $?; rm -rf ~/assembly-*', 'echo $?; touch ~/assembly-dotnet/x');
});
test('allows_ls_cat_du_find_grep', () => {
  allowed(`ls -la ${D}`, `cat ${D}/x`, `head -n 3 ${D}/x`, `tail ${D}/x`, `stat ${D}/x`, `du -sh ${D}`, `file ${D}/x`, `wc -l ${D}/x`,
    `find ${D} -name '*.db' -print`, `grep -rn needle ${D}/sub`, `rg needle ${D}`, `shasum ${D}/x`, `sha256sum ${D}/x`, `md5 ${D}/x`, `md5sum ${D}/x`);
});
test('allows_reading_output_through_pipes_and_redirects_outside', () => {
  allowed(`cat ${D}/x | head`, `cat ${D}/x > ${out}/x`, `ls ${D} 2>&1 | wc -l`, `cat ${D}/x >/dev/null 2>&1`, `cat < ${D}/x`);
});
test('allows_cp_and_rsync_out_to_a_temp_path', () => {
  allowed(`cp ${D}/x ${out}/x`, `cp -R ${D} ${out}/copy`, `cp ${D}/*.db ${out}/`, `cp -t ${out} ${D}/x`, `rsync -a ${D}/ ${out}/copy/`, `rsync -av --exclude 'logs' ${D}/ ${out}/copy`, `rsync -a ${D}/`);
});
test('allows_tar_create_out', () => {
  allowed(`tar -czf ${out}/live.tar.gz -C ${home} assembly-dotnet`, `tar czf ${out}/live.tar.gz ${D}`, `tar --create --file=${out}/live.tar ${D}`, `tar -cf - ${D} | tar -xf - -C ${out}`);
});
test('allows_sqlite3_readonly_select_and_backup_out', () => {
  allowed(`sqlite3 -readonly ${D}/a.db 'select count(*) from t'`, `sqlite3 -readonly -csv ${D}/a.db 'select name from sqlite_master'`,
    `sqlite3 -readonly ${D}/a.db ".backup ${out}/a.db"`, `sqlite3 -readonly ${D}/a.db .dump > ${out}/a.sql`, `sqlite3 -readonly ${D}/a.db '.schema'`);
});
test('allows_other_folders_that_share_the_prefix', () => {
  allowed(`rm -rf ~/assembly-dotnet-lane1/data`, `touch ${home}/assembly-dotnet-lane1/x`, `echo hi > $HOME/assembly-dotnet-lane1/log`, `cp ${D}/x ~/assembly-dotnet-lane1/x`, `sed -i s/a/b/ ~/.config/assembly-dotnet/x`);
});
test('allows_commands_that_do_not_name_the_folder', () => {
  allowed('rm -rf /tmp/x', 'git status', 'echo hello > /tmp/y', 'cd /tmp && touch z');
});
test('denies_rm_mv_sed_touch_tee', () => {
  denied(`rm ${D}/x`, `rm -rf ${D}`, `mv ${D}/x ${out}/`, `mv ${out}/x ${D}/x`, `sed -i s/a/b/ ${D}/x`, `sed -i '' s/a/b/ ${D}/x`, `touch ${D}/new`,
    `echo hi | tee ${D}/x`, `chmod 600 ${D}/x`, `chown me ${D}/x`, `mkdir ${D}/new`, `ln -s /tmp/a ${D}/link`, `dd if=/dev/zero of=${D}/x`, `truncate -s 0 ${D}/x`);
});
test('denies_redirect_into_the_folder', () => {
  denied(`echo hi > ${D}/x`, `echo hi >> ${D}/x`, `cat ${out}/a >| ${D}/x`, `echo hi &> ${D}/log`, `echo hi 2> ${D}/err`, `cat ${D}/x > ${D}/y`);
});
test('denies_cp_and_rsync_into_the_folder', () => {
  denied(`cp ${out}/x ${D}/x`, `cp ${D}/x ${D}/y`, `cp -t ${D} ${out}/x`, `cp -s ${D}/x ${out}/link`, `cp -l ${D}/x ${out}/link`, `rsync -a ${out}/ ${D}/`, `rsync -a ${D}/ ${D}-copy-of/ && rsync ${out}/ ${D}/sub/`);
});
test('denies_rsync_remove_and_delete_options', () => {
  denied(`rsync -a --delete ${D}/ ${out}/`, `rsync -a --delete-after ${D}/ ${out}/`, `rsync -a --remove-source-files ${D}/ ${out}/`, `rsync -a --link-dest=${D} ${D}/ ${out}/`, `rsync -a --log-file ${D}/log ${out}/ ${out}/b`);
});
test('denies_find_that_changes_things', () => {
  denied(`find ${D} -delete`, `find ${D} -name x -exec rm {} ;`, `find ${D} -execdir rm {} +`, `find ${D} -ok rm {} ;`, `find ${D} -fprint ${out}/list`, `find ${D} -fls ${out}/list`, `find ${D} -fprintf ${out}/l %p`);
});
test('denies_tar_that_writes_or_extracts_inside', () => {
  denied(`tar -cf ${D}/backup.tar ${out}`, `tar -xf ${out}/a.tar -C ${D}`, `tar -czf ${out}/a.tgz --remove-files ${D}`, `tar -cf ${out}/a.tar ${D} && tar -rf ${D}/a.tar x`, `tar -c ${D}`, `tar cf ${D}/a.tar x`);
});
test('denies_sqlite3_that_can_write', () => {
  denied(`sqlite3 ${D}/a.db 'select 1'`, `sqlite3 ${D}/a.db "insert into t values (1)"`, `sqlite3 -readonly ${D}/a.db 'DROP TABLE t'`, `sqlite3 -readonly ${D}/a.db 'delete from t'`,
    `sqlite3 -readonly ${D}/a.db "attach '${out}/b.db' as b"`, `sqlite3 -readonly ${D}/a.db ".backup ${D}/b.db"`, `sqlite3 -readonly ${D}/a.db ".open ${D}/b.db"`,
    `sqlite3 -readonly ${D}/a.db 'pragma journal_mode=wal'`, `sqlite3 -readonly -init ${D}/rc ${D}/a.db`, `echo 'drop table t' | sqlite3 -readonly ${D}/a.db`);
});
test('denies_git_and_other_commands_naming_the_folder', () => {
  denied(`git -C ${D} status`, `git -C ${D} checkout .`, `cd ${D} && git reset`, `python3 ${D}/x.py`, `open ${D}`, `echo ${D}`, `ssh box 'rm ~/assembly-dotnet/x'`);
});
test('denies_cd_then_destructive_commands', () => {
  denied(`cd ${D} && rm x`, `cd ${D}; touch y`, `cd ${D} && rm -rf sub`, `cd ${D}/sub && mv a b`, `cd ${home} && cd assembly-dotnet && rm x`, `cd ~ ; cd assembly-dotnet ; sed -i s/a/b/ x`);
});
test('denies_cd_then_relative_redirect', () => {
  denied(`cd ${D} && echo hi > x`, `cd ${D} && cat x > y`, `cd ${D}/sub && echo hi >> ../log`, `cd ${D} && cat ${out}/a > ./b`);
});
test('allows_cd_then_reads_and_outside_writes', () => {
  allowed(`cd ${D} && ls`, `cd ${D} && cat x | head`, `cd ${D} && cat x > ${out}/x`, `cd ${D} && cp x ${out}/x`, `cd ${D} && cd /tmp && touch z`);
});
test('denies_when_the_session_cwd_is_inside_the_folder', () => {
  const cwd = join(home, 'assembly-dotnet/sub');
  assert.deepEqual(checkDataCommand('rm x', cwd), { deny: DATA_DENY });
  assert.deepEqual(checkDataCommand('echo hi > y', cwd), { deny: DATA_DENY });
  assert.deepEqual(checkDataCommand('ls', cwd), {});
  assert.deepEqual(checkDataCommand('cd .. && rm x', cwd), { deny: DATA_DENY });
});
test('denies_command_substitution_and_backticks', () => {
  denied(`cp ${D}/x $(echo ${out})/x`, 'cat `echo ~/assembly-dotnet/x`', `ls ${D} $(rm ${D}/x)`, `cat <(rm ${D}/x)`, `echo "$(rm ${D}/x)"`);
});
test('denies_shells_interpreters_eval_and_xargs', () => {
  denied(`bash -c 'rm ${D}/x'`, `sh -c "rm ${D}/x"`, `zsh -c 'rm ${D}/x'`, `eval rm ${D}/x`, `python3 -c "import os; os.remove('${D}/x')"`, `node -e "require('fs').rmSync('${D}/x')"`,
    `perl -e 'unlink "${D}/x"'`, `find ${D} -name x | xargs rm`, `cat ${D}/x | sh`);
});
test('denies_variable_assignment_for_loop_and_unbalanced_quotes', () => {
  denied(`D=${D}; rm $D/x`, `D=${D} ls`, `export D=${D}`, `for f in ${D}/*; do rm $f; done`, `cat '${D}/x`, `cat "${D}/x`);
});
test('denies_glob_or_variable_in_destination', () => {
  denied(`cp ${D}/x ${out}/*`, `cp ${D}/x $OUT/x`, `cat ${D}/x > $OUT`, `cat ${D}/x > ${out}/*.txt`, `rsync -a ${D}/ $OUT/`, `tar -cf $OUT/a.tar ${D}`, `rm ${D}/*`);
});
test('denies_each_spelling_of_the_path', () => {
  for (const spelling of ['~/assembly-dotnet', '$HOME/assembly-dotnet', '${HOME}/assembly-dotnet', `${home}/assembly-dotnet`, '"$HOME"/assembly-dotnet', '~/Assembly-Dotnet', `${home}/./assembly-dotnet`, `${home}/x/../assembly-dotnet`, `'${home}/assembly-dotnet'`]) {
    denied(`rm ${spelling}/x`, `echo hi > ${spelling}/x`, `cp ${out}/a ${spelling}/`);
    allowed(`cat ${spelling}/x`, `cp ${spelling}/x ${out}/x`);
  }
});

test('denies_globs_that_can_reach_the_folder_without_naming_it', () => {
  denied('rm ~/assembly-d*/x', 'rm ~/*', 'cd ~ && rm -rf as*', 'rm -rf $HOME/as*', 'rm -rf ${HOME}/*', `rm ${home}/assembly-d?tnet/x`, `rm -rf ${home}/*`, `cd ${home} && rm -rf as*`,
    'rm ~/[a]ssembly-dotnet/x', `rm -rf ${dirname(home)}/*`, 'cp /tmp/a ~/as*', 'echo hi > ~/assembly-d*/x', 'cd ~ && mv as* /tmp/', 'find ~/as* -delete', 'cat ~/as* | xargs rm');
});
test('allows_reading_through_globs_and_unrelated_globs', () => {
  allowed('ls ~/assembly-d*', 'ls ~/*', 'cat ~/as*/x', 'cd ~ && ls as*', 'cp ~/assembly-d*/x ' + out + '/', 'rm ~/other/*', 'rm ~/*.txt', 'rm /tmp/*', `cd ${out} && rm *.log`,
    'ls *.ts $(pwd)', 'for f in *.md; do echo $f; done', 'cd "$X" && rm *.log');
});
test('treats_decoded_and_unquoted_words_as_the_protected_path', () => {
  denied('rm ~/assembly-"dotnet"/x', "rm ~/'assembly-dotnet'/x", 'rm ~/assembly-d\\otnet/x', 'rm "$HOME"/assembly-"dotnet"/x', `rm ${home}/as""sembly-dotnet/x`.replace('as""sembly', 'assembly'));
  allowed('cat ~/assembly-"dotnet"/x');
});
test('denies_rsync_that_runs_a_program_or_leaves_the_machine', () => {
  denied(`rsync -e ssh ${D}/ ${out}/`, `rsync -ae ssh ${D}/ ${out}/`, `rsync -a --rsh=ssh ${D}/ ${out}/`, `rsync -a --rsh ssh ${D}/ ${out}/`, `rsync -a --rsync-path=/bin/x ${D}/ ${out}/`,
    `rsync -a ${D}/ box:/tmp/copy/`, `rsync -a ${D}/ user@box:copy`, `rsync -a ${D}/ rsync://box/module/`, `rsync -a ${D}/ box::module`);
  allowed(`rsync -a ${D}/ ${out}/copy/`, `rsync -a /tmp/a box:/tmp/b`);
});

const flow = (t: any) => {
  const f = fixture(t);
  const pre = (command: string, extra: any = {}) => {
    const r = f.hook({ hook_event_name: 'PreToolUse', tool_name: 'Bash', tool_input: { command }, ...extra }, 'claude');
    assert.equal(r.status, 0, r.stderr); return JSON.parse(r.stdout);
  };
  return { ...f, pre };
};
test('cli_emits_pretooluse_deny_for_data_guard', t => {
  const f = flow(t);
  const result = f.pre(`rm ${f.home}/assembly-dotnet/x`);
  assert.deepEqual(result, { hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'deny', permissionDecisionReason: DATA_DENY } });
  assert.deepEqual(f.pre(`cp ${f.home}/assembly-dotnet/x ${f.home}/copy`), {});
  assert.deepEqual(f.pre('ls'), {});
});
test('cli_worktree_deny_still_wins_and_both_guards_apply', t => {
  const f = flow(t);
  const result = f.pre(`git worktree add /tmp/out && rm ${f.home}/assembly-dotnet/x`);
  assert.equal(result.hookSpecificOutput.permissionDecision, 'deny');
  assert.notEqual(result.hookSpecificOutput.permissionDecisionReason, DATA_DENY);
});
test('cli_denies_when_the_data_guard_throws_and_the_command_names_the_folder', t => {
  const f = flow(t);
  assert.equal(f.pre(`ls ${f.home}/assembly-dotnet`, { cwd: 5 }).hookSpecificOutput.permissionDecisionReason, DATA_DENY);
  assert.deepEqual(f.pre('ls /tmp', { cwd: 5 }), {});
});
test('fast_hook_prefilter_passes_payloads_naming_assembly_dotnet', t => {
  const f = fixture(t);
  const probe = join(f.bin, 'probe'); writeFileSync(probe, '#!/bin/sh\nprintf "invoked\\n"\n', { mode: 0o755 });
  const run = (command: string) => spawnSync('/bin/sh', [new URL('./fast-hook.sh', import.meta.url).pathname, 'pre', probe, 'cli.ts', 'claude'], { input: JSON.stringify({ tool_input: { command } }), encoding: 'utf8', env: f.env }).stdout;
  assert.equal(run('ls /tmp'), '{}\n');
  assert.equal(run('rm ~/assembly-dotnet/x'), 'invoked\n');
  assert.equal(run('rm ~/Assembly-Dotnet/x'), 'invoked\n');
  assert.equal(run('git worktree add /tmp/x'), 'invoked\n');
});
test('fast_hook_forwards_escaped_json_paths_to_node', t => {
  const f = fixture(t);
  const hook = new URL('./fast-hook.sh', import.meta.url).pathname, cli = new URL('./cli.ts', import.meta.url).pathname;
  const run = (raw: string) => {
    const r = spawnSync('/bin/sh', [hook, 'pre', process.execPath, cli, 'claude'], { input: raw, encoding: 'utf8', env: f.env });
    assert.equal(r.status, 0, r.stderr); return JSON.parse(r.stdout);
  };
  const payload = (commandJson: string) => `{"hook_event_name":"PreToolUse","session_id":"s","cwd":"${f.home}","tool_input":{"command":"${commandJson}"}}`;
  const deny = { hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'deny', permissionDecisionReason: DATA_DENY } };
  // Literal escaped JSON, exactly as a harness may send it.
  assert.deepEqual(run(payload('rm ~\\/assembly-dotnet\\/x')), deny);
  assert.deepEqual(run(payload('rm ~/assembly-\\"dotnet\\"/x')), deny);
  assert.deepEqual(run(payload('rm ~/assembly-d\\u006ftnet/x')), deny);
  assert.deepEqual(run(payload('rm ~/assembly-dotnet/x')), deny);
  assert.deepEqual(run(payload('rm ~/*')), deny);
  assert.deepEqual(run(payload('cd ~ && rm -rf as*')), deny);
  assert.deepEqual(run(payload('ls ~/assembly-d*')), {});
});
test('fast_hook_keeps_plain_commands_on_the_fast_path', t => {
  const f = fixture(t);
  const probe = join(f.bin, 'probe'); writeFileSync(probe, '#!/bin/sh\nprintf "invoked\\n"\n', { mode: 0o755 });
  const run = (raw: string) => spawnSync('/bin/sh', [new URL('./fast-hook.sh', import.meta.url).pathname, 'pre', probe, 'cli.ts', 'claude'], { input: raw, encoding: 'utf8', env: f.env }).stdout;
  const payload = (command: string) => JSON.stringify({ tool_input: { command } });
  assert.equal(run(payload('ls /tmp')), '{}\n');
  assert.equal(run(payload('rm *.log')), '{}\n');
  assert.equal(run(payload('ls /tmp/a?')), '{}\n');
  assert.equal(run(payload('rm ~/*')), 'invoked\n');
  assert.equal(run(payload('rm $HOME/as*')), 'invoked\n');
  assert.equal(run(payload('rm /Users/me/as*')), 'invoked\n');
  assert.equal(run(payload('rm /home/me/as?')), 'invoked\n');
  assert.equal(run(payload('echo "a"')), 'invoked\n');
});
