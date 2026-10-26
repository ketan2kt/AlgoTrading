# Public contact form setup

The new Sarthico homepage is public at `/`; `/nifty` and `/sensex` retain their existing sign-in. The form stores verified enquiries in `trading.contact_inquiries` and a background worker retries SMTP notification delivery. It fails closed until every setting below is available. No trading setting is changed.

## Google reCAPTCHA

Use the Google Cloud Fraud Defense console under the Google account chosen for Sarthico. Create a web **Challenge (v2) / checkbox** key limited to `sarthico.com` (and `www.sarthico.com` only if that host is actually used). Choose the Essentials tier if its allowance suits expected traffic. Create a separate Google Cloud API key in the same project, restricted to the reCAPTCHA Enterprise API. The backend sends the token to `projects.assessments.create`, checks validity and exact hostname, and never sends the API key to the browser. Google currently provides 10,000 free assessments per organization per month; without billing, further Essentials assessments receive a quota error until reset. Do not enable billing without reviewing that choice.

Set these Azure App Service application settings (or local user secrets for development):

| Setting | Value |
| --- | --- |
| `PublicContact__RecaptchaSiteKey` | Public site key / key ID of the checkbox widget |
| `PublicContact__RecaptchaProjectId` | Google Cloud project ID |
| `PublicContact__RecaptchaApiKey` | Server API key, restricted to the reCAPTCHA Enterprise API |
| `PublicContact__ExpectedHostname` | `sarthico.com` (default) |

## Gmail sender

The selected sender and recipient are the same Gmail account supplied by Ketan. In that Google account, enable 2-Step Verification and create an **app password** for this application if Google makes that option available. Do not use the normal account password. Set the app password directly in Azure App Settings; never paste it into chat, commit it, or log it. If Google does not offer app passwords for the account, choose another SMTP provider and set its host, port, sender, username and password instead.

| Setting | Value |
| --- | --- |
| `PublicContact__RecipientEmail` | Ketan's selected Gmail address |
| `PublicContact__SmtpHost` | `smtp.gmail.com` |
| `PublicContact__SmtpPort` | `587` (STARTTLS; `465` SSL is also supported) |
| `PublicContact__SmtpUsername` | Same Gmail address |
| `PublicContact__SmtpPassword` | Google-issued app password |
| `PublicContact__SmtpFromEmail` | Same Gmail address |

The form is enabled only when all settings are present. After deployment and configuration, `GET /api/public/contact/config` should report `enabled: true` and only the public site key. Submit one real test enquiry through the form and confirm a database record and received email. Never test by bypassing CAPTCHA. The API limits submissions per source IP and stores no CAPTCHA token or source IP with the enquiry. Delivery is queued after database persistence, so the acceptance response means the enquiry was saved, not that email has already arrived.
