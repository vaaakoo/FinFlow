# Architecture decisions

## Keep financial postings in the Ledger

The Ledger commits both account updates, immutable entries, and an outbox row in one PostgreSQL transaction. The Transfer API owns request lifecycle and calls the Ledger with a stable operation ID. Repeating an uncertain call with the same identifier retrieves the original posting. Distributed orchestration is reserved for workflows with independent participants, such as simulated external payments.

## Add a secured profile around the existing demo

The base Compose project preserves the original loopback transfer demo. The secured Compose project removes the direct ledger port and adds a Keycloak issuer and YARP gateway. Each HTTP service validates its own bearer tokens. Ledger customer queries bind account visibility to the authenticated subject. Existing synthetic seed accounts are staff-visible only.

## Use database constraints for posting integrity

The application acquires account locks in stable order and checks exact minor-unit amounts. PostgreSQL separately checks each transfer posting at commit and rejects updates or deletes of posted transfers and entries. Future reversals will write compensating postings that reference the original operation.

