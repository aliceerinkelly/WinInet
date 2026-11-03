# WinInet
a simple ftp client that doeant ever require install

## Code signing policy

Release binaries for this project are signed through SignPath Foundation.

- **What is signed:** only artifacts built from this repository's own source at a tagged release, by CI.
- **What is not signed:** binaries from other projects, builds produced outside CI, and any commercially distributed build.
- **Approval:** every signing request is approved by a project Approver before the key is used.
- **Roles:** Authors have commit access; Reviewers review pull requests from non-committers; Approvers authorise signing.
- **Security:** all maintainers use multi-factor authentication on GitHub and SignPath.
- **Key handling:** the private key lives on SignPath's HSM and is never present on a build machine.
- **Verification:** each signed release states the signing identity and the commit it was built from.
