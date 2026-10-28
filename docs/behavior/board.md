# Board behavior

Draft; awaiting correction. Tests remain in Draft.

board#1 Only a token-owned, non-helper session can report its own task.
board#2 Progress updates retain one card; identical progress creates no event.
board#3 Independent questions, generation checks and immutable answer identities prevent lost answers.
board#4 Persist answers before send; restart retains unknown delivery without resending.
board#5 SQL orders, filters, counts and pages tasks, questions and feed; unknown metrics remain null.
board#6 Source replay copies complete events atomically and retains remote cards offline.
board#7 Source-directed detail and answers preserve configured machine identity.
board#8 Subscribe before reads; publish only after commit; closed views do not stop backend mirroring.
