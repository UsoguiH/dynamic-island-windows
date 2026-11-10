# Security

Island runs on your PC only. It has no server, no account and no telemetry, so the things worth reporting are mostly about what it does on your machine:

- reading or writing files it shouldn't
- running a command you didn't ask for
- a described tab's recipe reaching somewhere it shouldn't
- the installer or the self-update fetching anything other than this repository's releases

## Reporting a problem

Please **don't open a public issue** for a security problem.

Use GitHub's private report instead: on this repository open **Security → Report a vulnerability**. Say what you did, what happened, and which release you downloaded.

You'll get an answer within a few days. Fixes ship as a normal release, and installed copies pick them up by themselves.

## Supported versions

Only the latest release gets fixes. Island updates itself, so that is the version almost everyone runs.
