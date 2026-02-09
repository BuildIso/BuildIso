# Security Policy

Security Policy
Supported Versions

Security updates apply only to the latest stable release of BuildIso. Older versions may not receive patches, fixes, or investigations unless the issue is critical and reproducible.

## Version	Supported
v2026.2	Yes
v2026.1 and older	No
Scope of This Document

This Security Policy is intended for developers and contributors, not end-users. It covers:

How to report vulnerabilities

How security issues are handled

Expectations for responsible disclosure

Verification of legitimate sources

Integrity checks for distributed binaries

This document does not explain how to use BuildIso or how to run the executable.

Reporting a Vulnerability

If you discover a security issue, follow responsible disclosure:

Do not open a public GitHub issue.

Contact the maintainer privately through GitHub (direct message or private issue).

## Provide:

A clear description of the vulnerability

Steps to reproduce the issue

Affected version(s)

Potential impact

The maintainer will acknowledge the report and begin investigation as soon as possible.

Handling of Security Issues

When a vulnerability is confirmed:

It is investigated privately.

A fix is prepared and tested.

A patched release is published.

A short security advisory may be added to the release notes.

Security issues are not discussed publicly until a fix is available.

## Official Sources

To ensure authenticity of BuildIso binaries, only download from:

Active repository: https://github.com/BuildIso/BuildIso

Archived mirror: https://github.com/htmluser-hub/BuildIso

Official website: https://buildiso.com

Additional mirror: https://srcfrcg.itch.io/buildiso
 (may host older versions)

Any executable obtained outside these sources should be considered untrusted.

## Integrity Verification

Each release includes an official SHA256 checksum. Developers should verify the integrity of downloaded binaries before execution.

Example (v2026.2):

86d976bede96cdb9d0e731eae1aad394f6231e1ec4f02549d178b6a8fc7c83f0


If the hash does not match, do not run the file.

**Administrator Privileges**

BuildIso does not require **administrator** rights. Running the executable with elevated privileges is discouraged unless absolutely necessary for development or testing.

Unsafe Environments

Avoid running BuildIso from:

Protected system directories

Unknown or untrusted machines

Shared environments without isolation

These practices reduce the risk of unintended access or privilege escalation.

Final Notes

This Security Policy ensures that BuildIso remains safe, transparent, and trustworthy for developers. By following responsible disclosure and verifying official sources, contributors help maintain the integrity of the project.
