# 01.03-final-validation: Validate connector upgrade and outage recovery

## Objective
Revalidate the MySql.Data 26.10.0 upgrade and all approved warning fixes end to end.

## Scope
Build the solution without warnings and run application, web, and opt-in Release disposable Docker MySQL tests. Verify startup outage and TCP recovery safely with synthetic loopback configuration. Document Oracle page, production server/TLS, actual reconnect and browser limitations. Never use real monetary data.

## Done when
The solution builds warning-free, all applicable tests pass, outage/recovery checks pass, and limitations are recorded.
