Inspect the existing provider integrations and autonomously select one that could benefit from maintenance.

Before selecting work, inspect all open pull requests and relevant issues. Review their scope and changed files to identify work already in progress. Do not duplicate, overlap with, or independently reimplement fixes or improvements covered by existing pull requests.

Review the selected integration's implementation, research the latest official API documentation where relevant, and identify bugs, inconsistencies, outdated behavior, or opportunities for improvement.

Apply well-justified fixes and improvements, including issues discovered independently of API documentation.

Do not modify shared contracts, public interfaces, or API schemas. Do not invent capabilities, assumptions, or unnecessary abstractions. Preserve compatibility and existing behavior.

Add or update relevant tests and verify the build.

If no worthwhile independent improvements remain, make no changes. Report your findings, reasoning, and any changes made.
