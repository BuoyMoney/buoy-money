# Plaid Webhook Security Implementation

## Overview
The Plaid webhook endpoint has been secured with proper signature validation to prevent unauthorized webhook calls while maintaining the anonymous access required for Plaid's webhook delivery system.

## Security Implementation

### ValidatePlaidWebhookSignatureAttribute
A custom action filter attribute that validates incoming Plaid webhooks using **JWT/JWK signature verification** (Plaid's documented scheme) — **not** a shared HMAC secret.

**Features:**
- **JWT/JWK Verification**: Plaid signs each webhook with a JWT (ES256) delivered in the `Plaid-Verification` header; we verify it against Plaid's published JWK.
- **Body-hash check**: Verifies the request-body hash claim in the JWT matches the received body.
- **JWK caching**: Fetched keys are cached (`plaid_jwk_*`) to avoid a key round-trip per webhook.
- **Comprehensive Logging**: Detailed security logging for monitoring and debugging.
- **Error Handling**: Graceful handling of malformed/missing tokens and headers.

### How It Works

1. **JWT Extraction**: Reads the signed JWT from the `Plaid-Verification` header.
2. **Key ID**: Decodes the JWT header (without validating) to get the key id (`kid`).
3. **JWK Fetch**: Retrieves the matching public key from Plaid (cached) for that `kid`.
4. **Signature + Body Verification**: Verifies the JWT signature with the JWK, then checks the JWT's body-hash claim against the received request body.
5. **Authorization Decision**: Allows or denies the request based on verification.

### Configuration Required

**No shared webhook secret** — verification uses Plaid's published JWK, fetched at runtime. The Plaid config just needs the standard credentials:

```json
{
  "Plaid": {
    "Environment": "sandbox",
    "ClientId": "your_client_id",
    "SecretKey": "your_secret_key",
    "ClientName": "BuoyAI"
  }
}
```

### Usage

The validation attribute is applied to the webhook endpoint:

```csharp
[HttpPost("receive")]
[AllowAnonymous] // Plaid webhooks don't use our auth but are validated via signature
[ValidatePlaidWebhookSignature] // Custom validation for webhook authenticity
public async Task<IActionResult> ReceiveWebhook([FromBody] PlaidWebhookRequest webhook)
```

### Properties

- Only requests signed by Plaid are processed; the endpoint stays anonymous, as Plaid requires.
- Nothing to rotate: the verification key comes from Plaid's JWK endpoint.
- Tests live in `tests/BuoyAI.UnitTests/Attributes/ValidatePlaidWebhookSignatureAttributeTests.cs`
  (valid signature, invalid signature, missing header).
