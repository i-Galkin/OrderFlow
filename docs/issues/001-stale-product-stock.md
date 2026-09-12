# 001 - Product endpoint reports stock that no longer exists

**Reported by:** Catalogue team
**Severity:** Medium
**Status:** Open

## Symptoms

`GET /api/products/{id}` keeps returning the old `stockQuantity` after orders for that product
have been processed. The storefront shows items as available that the warehouse has already
committed to other orders.

## Steps to reproduce

1. Note the stock of any product: `GET /api/products/{id}` -> `stockQuantity: 40`.
2. Create and confirm an order for 5 units of it.
3. Wait for the worker to log `Reserved 1 lines for order ...`.
4. Query the database directly: `SELECT "StockQuantity" FROM products WHERE "Id" = ...` -> 35.
5. `GET /api/products/{id}` still returns 40.

## Notes

* The value corrects itself if you wait a while before querying again, roughly ten minutes.
* Restarting the API does not change anything; flushing Redis does.
* A product that has never been requested through the API shows the correct number the first
  time it is requested.
* Creating a product through `POST /api/products` and then reading it back always matches.

## Impact

Oversell complaints from customers ordering low-stock items, and the catalogue team no longer
trusts the API for stock reporting.
