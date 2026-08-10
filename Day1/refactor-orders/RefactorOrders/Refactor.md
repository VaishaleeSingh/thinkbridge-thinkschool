# Refactor Notes

Before changing the legacy OrderController, I identified the following smells and risks.

## 1. Giant controller action

**Smell:** `CreateOrder()` contains validation, customer lookup, discount calculation, product lookup, stock management, tax calculation, order creation, loyalty points, email sending, invoice generation, logging, and response creation.

**Consequence:** The method is difficult to understand, test, maintain, and modify safely.

**Intended fix:** Move business logic into an `OrderService`, database operations into an `OrderRepository`/repositories, and keep the controller focused on HTTP concerns.

## 2. Dynamic request model

**Smell:** The POST action accepts `dynamic orderRequest`.

**Consequence:** There is no compile-time type safety. Invalid or missing properties are discovered only at runtime and require unsafe casts.

**Intended fix:** Introduce a strongly typed `CreateOrderRequest` DTO with validation.

## 3. Exceptions are swallowed

**Smell:** Several `catch { }` blocks silently ignore exceptions when reading request fields.

**Consequence:** Real errors are hidden and debugging becomes difficult. Invalid input can silently turn into default values.

**Intended fix:** Remove unnecessary try/catch blocks or catch only specific expected exceptions, log them, and handle them deliberately.

## 4. Synchronous EF Core calls inside async code

**Smell:** The async `CreateOrder()` action uses synchronous EF calls such as `FirstOrDefault()` and `SaveChanges()`.

**Consequence:** Database threads can be blocked under load, reducing scalability.

**Intended fix:** Use `FirstOrDefaultAsync()` and `SaveChangesAsync()` and pass a `CancellationToken` through the entire call chain.

## 5. No cancellation token

**Smell:** Database operations do not accept or use a cancellation token.

**Consequence:** Work can continue even after the client disconnects or cancels the request.

**Intended fix:** Accept `CancellationToken` in controller/service methods and pass it to EF Core async operations.

## 6. Business logic in the controller

**Smell:** Discount, tax, stock, loyalty points, and order calculations are implemented directly in the controller.

**Consequence:** Business rules are tightly coupled to HTTP and are difficult to unit test independently.

**Intended fix:** Move business rules into an `OrderService` or appropriate domain/service classes.

## 7. Database access directly from the controller

**Smell:** The controller directly uses `_context.Customers`, `_context.Products`, and `_context.Orders`.

**Consequence:** The controller is coupled to EF Core and database implementation details.

**Intended fix:** Introduce repository abstractions and inject them through DI.

## 8. Multiple database saves inside one order operation

**Smell:** `_context.SaveChanges()` is called while processing each product and again when creating the order and updating loyalty points.

**Consequence:** A failure halfway through can leave stock, orders, and loyalty points in an inconsistent state.

**Intended fix:** Use a transaction/unit-of-work approach and save the complete operation atomically.

## 9. N+1 database queries

**Smell:** Every order item performs a separate product lookup, and cancellation also looks up products one at a time.

**Consequence:** Database round trips increase with the number of items and can hurt performance.

**Intended fix:** Fetch required products in batches or use a repository method designed for the complete set of SKUs.

## 10. Off-by-one bug

**Smell:** The item loop uses `i <= items.Count`.

**Consequence:** When `i == items.Count`, `items[i]` is outside the valid range and causes an `IndexOutOfRangeException`.

**Intended fix:** Change the loop condition to `i < items.Count`, or preferably use `foreach`.

## 11. Possible null dereference

**Smell:** The response accesses `customer.Tier` even though `customer` can be null.

**Consequence:** A new customer can cause a `NullReferenceException` after the order has already been created.

**Intended fix:** Handle the null customer explicitly and use a safe nullable value or create/resolve the customer before building the response.

## 12. Untyped HTTP responses

**Smell:** Methods return `object` and anonymous objects.

**Consequence:** The API contract is unclear and compile-time response typing is lost.

**Intended fix:** Use typed DTOs and return `ActionResult<T>` or appropriately typed minimal/controller responses.

## 13. Hardcoded configuration and credentials

**Smell:** SMTP host, email address, and especially the password are hardcoded in source code.

**Consequence:** This is a security risk and makes configuration changes require code changes.

**Intended fix:** Move configuration to options/configuration or a secret store and inject the required settings.

## 14. Console.WriteLine used for application logging

**Smell:** The controller uses `Console.WriteLine()` for warnings and order logs.

**Consequence:** Logs are not structured and are harder to search, filter, correlate, or integrate with production logging systems.

**Intended fix:** Inject `ILogger<OrderController>` or preferably log from the appropriate service layer using structured logging.

## 15. Hardcoded business rules

**Smell:** Discount tiers and tax rates are hardcoded string/decimal values inside the controller.

**Consequence:** Business rule changes require modifying and redeploying controller code, and the rules are difficult to test independently.

**Intended fix:** Move pricing/tax rules into dedicated services or configuration-backed components.

## 16. Magic strings

**Smell:** Values such as `"Gold"`, `"Silver"`, `"Bronze"`, `"Pending"`, and `"Cancelled"` are repeated as raw strings.

**Consequence:** Typos can introduce bugs and the meaning of these states is not represented clearly in the type system.

**Intended fix:** Use enums/value objects or constants where appropriate.

## 17. DateTime.Now is used directly

**Smell:** `DateTime.Now` is used for order creation and invoice generation.

**Consequence:** The code is harder to test consistently and can behave differently across environments/time zones.

**Intended fix:** Inject a clock/time abstraction or use a consistent UTC-based time source.

## 18. Email sending is mixed into order creation

**Smell:** SMTP setup, credentials, message construction, and sending are all inside the controller.

**Consequence:** The controller becomes difficult to test and an email provider change requires modifying controller code.

**Intended fix:** Introduce an email/notification service behind an interface and inject it through DI.

## 19. Email failure is silently hidden from the API flow

**Smell:** Email exceptions are caught and only written to the console.

**Consequence:** The system can report a successful order without clearly tracking that notification failed.

**Intended fix:** Catch a specific email-related exception, log it with `ILogger`, and decide explicitly whether notification failure should affect the request or be handled asynchronously.

## 20. Validation is manual and scattered

**Smell:** Request fields are extracted and validated individually inside the controller.

**Consequence:** Validation becomes repetitive and difficult to reuse or test.

**Intended fix:** Use strongly typed request DTOs with validation and return proper validation responses.

## 21. No automated tests

**Smell:** The generated code has no unit or integration tests.

**Consequence:** Refactoring the large controller has a high risk of changing behavior without detecting it.

**Intended fix:** Add at least 3 unit tests for service/business behavior and 1 `WebApplicationFactory` integration test covering the API.

## 22. Too much work happens before the final response

**Smell:** One HTTP request performs validation, multiple database operations, inventory updates, order persistence, loyalty updates, email sending, invoice generation, and response mapping.

**Consequence:** The endpoint has a large failure surface and is difficult to reason about.

**Intended fix:** Separate responsibilities into controller, service, repository, and notification components with clear boundaries.
