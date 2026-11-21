# Screenshots and videos for a PR

Proof that shows the window (screenshots, frame strips, short videos) goes to Google Drive, never into this repo, and
the PR and its Notion task link to it. `evidence/<commit>/four-way/` is separate: that's the check summary, and it is
committed.

## Where it runs

On agent-workbox. The `evidence` tool (`~/.local/bin/evidence`) and the rclone remote `gdrive` that it uploads
through are set up there. The Mac has rclone but no `gdrive` remote, and no `evidence`. `~/Workbox/` syncs between the
Mac and agent-workbox, so a file saved on the Mac under `~/Workbox/screenshots/` shows up on agent-workbox. You can
also copy it over with `scp`.

## Steps

1. Name the file after the task: `~/Workbox/screenshots/TSK-<id>-<what>.png` (or `.mp4`). A file outside that folder
   is copied in with a `tsk-<id>-` prefix.
2. On agent-workbox, from the branch's worktree (so it finds the PR):

   ```sh
   evidence add <file> --task TSK-<id> --desc "<what it proves>" [--key]
   ```

   This uploads to `gdrive:UI Evidence/<repo>/TSK-<id>/`, prints the file's link and the folder's link, and writes a
   `### Proof` block into the open PR's body. Pass `--project Assembly` when you run it outside the repo.
3. `evidence publish TSK-<id>` uploads every `--key` file for the task again and prints all the links: paste them
   into the task.

Links: an image is `https://drive.google.com/uc?export=view&id=<file id>`; a video is
`https://drive.google.com/file/d/<file id>/view`; the folder is `https://drive.google.com/drive/folders/<id>`.

A PR without a Notion task can't use `evidence`, because `--task` must be `TSK-<digits>`. Ask for the task id.

Keep proof small: at most 4 screenshots and one video of 2 minutes or less per PR.
