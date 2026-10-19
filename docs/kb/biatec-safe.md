# Biatec Safe

A Biatec Safe (Algo Safe) is a shared smart account on Algorand. A group of signers controls it with an M-of-N rule, for example 2-of-2 or 2-of-3: a transaction only executes after M signers approved it.

The flow is propose, approve, execute. One member proposes a transaction, other members approve it from their own sessions, and once enough approvals exist it can be executed. A safe can also hold spending limits and policies.

Creating or managing a safe needs a signed-in account.
