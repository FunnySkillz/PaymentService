# Stripe + ASP.NET Core (.NET) — LTS v49

Production-safe integration using Stripe.net **49.x (LTS)**:
- Checkout Sessions for one-time
- Subscriptions with webhook handling
- v49-compatible invoice handling (payments + confirmation_secret)

## Configuration
Copy `appsettings.json.example` to `appsettings.json` and set keys via:
```bash
dotnet user-secrets set "Stripe:SecretKey" "..."
dotnet user-secrets set "Stripe:PublishableKey" "..."
dotnet user-secrets set "Stripe:WebhookSecret" "..."
