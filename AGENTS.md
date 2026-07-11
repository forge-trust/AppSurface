# AGENTS.md

## Modification Guidelines

These guidelines apply to all changes made in this repository.

### Documentation

- Fully document all API surface areas affected by a change, including both external and internal APIs.
- Treat documentation as part of the feature, not as optional follow-up work.
- Include reference content that explains the API shape, behavior, defaults, constraints, and expected usage.
- Include decision content that explains why an API exists, when it should be used, and when a different approach is the better choice.
- Include pitfall content that calls out sharp edges, ordering requirements, surprising behavior, and common mistakes.
- Update package-level and repository-level documentation when a change affects discoverability or adoption, not just inline XML comments.
- Link named concepts to their canonical documentation instead of only mentioning them. When prose names a package, guide, example, workflow, policy, diagnostic family, CLI command, or cross-package concept, add a nearby link to the best start-here or reference page unless the same paragraph already defines it completely.
- Prefer a small set of useful cross-links over scattered repeats. Link the first meaningful mention in a section, and link again only when the reader is likely to need the destination without scrolling back.

### Tests

- Aim for nearly 100% branch verification coverage, especially in changed code and critical execution paths.
- Test both success paths and failure or edge-case branches.
- Test behavior through public APIs and internal APIs that are intentionally exposed for testing.
- Do not use reflection in tests to access private members.
- If code is difficult to verify without reflection, improve the design or add an appropriate test seam instead of breaking encapsulation.
- Add regression tests for bug fixes.
- When practical, verify solution-level coverage with `./scripts/coverage-solution.sh`.

### Code Quality

- Run code formatting before pushing changes.
- Follow the repository `.editorconfig` and established formatting conventions rather than personal preferences.
- Resolve all compiler, analyzer, and documentation warnings introduced by a change before pushing.
- Prefer changes that reduce existing warnings in touched areas instead of working around them or suppressing them without strong justification.
- Follow current C# and .NET best practices so code is clear, idiomatic, and maintainable.
