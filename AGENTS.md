# FinFlow repository instructions

- Use English and synthetic data. Keep source MIT-licensed and verify new dependency licenses and version compatibility.
- Maintain separate service databases. Share integration contracts and technical infrastructure only.
- Keep ledger postings atomic, balanced, immutable, and safe under concurrency. Use compensating entries and exact minor units.
- Preserve the v0.1 Compose workflow as a local demo. Validate bearer tokens at each secured HTTP boundary and check resource ownership at the owning service.
- Use durable at-least-once messaging with transactional outbox, acknowledgments after commit, and persistent deduplication.
- Follow PROJECT_PLAN.md and update PROJECT_STATUS.md with verified checks and outstanding work. Run focused tests after material changes.
- Keep real credentials, real customer data, and real payment integrations out of the repository. Obtain explicit authorization before publishing or deploying externally.

## Code readability

- Write conventional C# that a .NET developer can read, debug, and extend. Choose meaningful type, method, and variable names.
- Prefer explicit control flow and simple queries. Avoid nested ternaries, dense LINQ, and pattern matching that hides the business rule.
- Keep methods focused. Introduce interfaces, generic types, factories, wrappers, or layers only when they solve a current problem.
- Use modern C# features when they improve readability. Comment business rules and technical decisions that the code cannot explain on its own.
- Keep validation and error handling proportional to the operation. Preserve security, performance, service boundaries, and financial consistency.
- Simplify relevant code as you work. Preserve the roadmap and existing behavior.

