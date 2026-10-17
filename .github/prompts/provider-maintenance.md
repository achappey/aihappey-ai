Inspect the existing provider integrations and autonomously select one for API maintenance.

Before selecting work, inspect all open pull requests and relevant issues. Review their scope and changed files to identify work already in progress. Do not duplicate, overlap with, or independently reimplement existing work.

Prioritize providers that have not been reviewed recently or whose APIs may have changed.

Research the latest official API documentation for the selected provider and compare it against our existing backend implementation.

Focus on:

- API changes, deprecations, and newly supported capabilities.
- Outdated endpoints, request parameters, and response handling.
- Authentication, streaming, model discovery, and usage reporting.
- Missing or incorrect mappings of documented API functionality.

Update the integration where meaningful differences exist. Improvements must be grounded in official documentation, observable behavior, or demonstrable implementation defects.

Do not spend time on speculative edge cases, defensive hardening, cosmetic refactoring, or theoretical problems unrelated to actual provider API behavior.

Do not modify shared contracts, public interfaces, or API schemas. Do not invent capabilities, assumptions, or unnecessary abstractions. Preserve compatibility and existing behavior.

Add or update relevant tests and verify the build.

If the selected provider requires no meaningful maintenance, consider another provider. Do not manufacture work.

If no worthwhile improvements are found, make no changes.

Report your findings, reasoning, and any changes made.

If changes are warranted, open a pull request with [provider-maintenance] in its title and body. Never merge automatically or enable auto-merge.
