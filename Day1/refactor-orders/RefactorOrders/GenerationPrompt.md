Create a deliberately bad legacy-style OrderController.cs for an ASP.NET Core 10 Web API.

Requirements:

- Around 300 lines of code.
- One giant POST /api/orders action.
- Mix business logic, EF Core database access, validation, and HTTP response shaping directly inside the action.
- Use dependency injection only minimally.
- Include four empty catch { } blocks that swallow exceptions.
- Use synchronous EF Core calls such as ToList(), FirstOrDefault(), SaveChanges(), etc. inside an async action.
- Return object instead of typed IActionResult/ActionResult<T> responses.
- No tests.
- Include several realistic code smells.
- Include at least two subtle bugs:
  1. an off-by-one error
  2. a possible null dereference
- Make the code look like something a developer might realistically find in an old production codebase.
- Do NOT refactor or improve the code.
- Do NOT explain the smells.
- Generate only the source code for OrderController.cs.

The code should be syntactically plausible ASP.NET Core 10 code and should reference EF Core and realistic Order/OrderItem models where necessary.
