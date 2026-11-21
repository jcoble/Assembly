Draft tests: written from a behavior list Jesse hasn't corrected yet (a page under the [Assembly documentation](https://app.notion.com/p/409394b0689d8197a38efdfb8d400f84)
in Notion). One folder per feature, namespace `Assembly.Tests.Draft.<Feature>`. They aren't locked and follow the
normal test rules. Each names the list line it tests (`// Pins: base#2`). Once Jesse has corrected the list,
`scripts/pin.sh <feature> <link to the behavior list page>` moves them into `Pinned/`, after any changes the correction
needs. The script can't read Notion, so whoever runs it has checked that Jesse corrected the list; the link goes in the
pin commit's message.
