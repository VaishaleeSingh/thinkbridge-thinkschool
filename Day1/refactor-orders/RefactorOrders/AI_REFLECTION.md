# AI Reflection

This exercise showed me that AI can speed up refactoring, but I still need to understand and review every change before accepting it.

Claude got the main refactoring task right. It identified the discount and tax logic inside OrderService and moved those rules into separate Strategy Pattern classes. The existing business behaviour was preserved: Gold, Silver, and Bronze discounts remained 15%, 10%, and 5%, while the existing state tax rules were also kept unchanged. I reviewed the diff before accepting the refactor and checked that it did not introduce unnecessary factories or complicated abstractions.

The main bug I would watch for is an accidental change in business rules while moving code. For example, changing a tax percentage or discount value could compile successfully while silently changing production behaviour. The existing tests helped verify that the refactor did not break the current functionality.

The AI assistant was especially useful for generating validation tests. It suggested tests for negative quantity, zero quantity, and an empty customer email. I reviewed each suggestion before applying it and then ran the complete test suite. All seven tests passed. One thing I noticed is that AI-generated tests can contain unnecessary setup or assumptions, so the suggestion still needs human review.

The GitHub Copilot extension was not available in my IDE, so I used the IDE's built-in AI assistant for the test-generation portion instead. I would use AI first at 2 AM to help investigate a production issue and generate ideas, but I would rely on logs, tests, and the actual code before deciding on a fix.
