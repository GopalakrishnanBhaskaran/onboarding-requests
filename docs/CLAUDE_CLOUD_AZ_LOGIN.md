# Azure CLI sign-in in Claude's cloud environment (PEST and Vestacy)

Claude runs the `az` commands in its own cloud container. You do only the browser sign-in.
Each tenant has its own sign-in, and each environment (dev, test, prod) has its own az profile, so PEST and Vestacy never mix.
Tool: `tools/az_cloud_login.sh`. It changes nothing in Azure.

## Once: environment variables

Set these in the Claude cloud environment: session title bar > environment menu > **Edit** > **Environment variables**.
They are names or ids, not secrets. Never put a password or a key there. A new session picks them up.

```
PEST_TENANT=<PEST tenant id or domain>
PEST_SUB_DEV=<subscription name or id>
PEST_SUB_TEST=<subscription name or id>
PEST_SUB_PROD=<subscription name or id>
VESTACY_TENANT=<Vestacy tenant id or domain>
VESTACY_SUB_DEV=<subscription name or id>
VESTACY_SUB_TEST=<subscription name or id>
VESTACY_SUB_PROD=<subscription name or id>
```

- Leave an environment empty if it doesn't exist. It then gets no profile.
- Don't know the names yet? Sign in first; `finish` lists the subscriptions you can see.

## Every new session: sign in (about 1 minute per tenant)

| Step | Who | What |
|---|---|---|
| 1 | Claude | `bash tools/az_cloud_login.sh login pest`: shows a URL and a one-time code |
| 2 | You | Open https://microsoft.com/devicelogin in your browser, enter the code, sign in with the **PEST** account and do MFA. Then tell Claude "done" |
| 3 | Claude | `bash tools/az_cloud_login.sh finish pest`: checks the tenant and makes the `pest-dev`, `pest-test` and `pest-prod` profiles |
| 4 | Both | Same for Vestacy: `login vestacy`, your browser sign-in with the **Vestacy** account, then `finish vestacy` |
| 5 | Claude | `bash tools/az_cloud_login.sh status`: one line per tenant and environment, OK / WAITING / EXPIRED |

Claude then runs each command in one environment:

```bash
export AZURE_CONFIG_DIR="$(bash tools/az_cloud_login.sh dir pest dev)" && az account show -o table
```

## Guards

- **Wrong tenant:** `finish` stops if you signed in with the wrong account. This applies when `<TENANT>_TENANT` is set, or when the same tenant is already signed in under the other name.
- **Wrong subscription:** a subscription that is not visible, or is in another tenant, gets no profile.
- **Prod:** the `dir … prod` profile prints a PROD note. Every change still needs the dry run and an explicit "approved" (CLAUDE.md rule 4).
- **Nothing exposed:** output masks ids to 4 characters. The one-time code is meant for you. No password or token is shown or stored outside the az profile.

## Limits

- **Container lifetime:** the container is reclaimed after inactivity, and the sign-ins go with it. A new session repeats steps 1–5.
- **Device-code may be blocked:** Conditional Access can block device-code sign-in, and Microsoft recommends blocking it where possible (F114). If `login` or your browser shows "You don't have access to this", AADSTS50199 or a Conditional Access message, this route is closed in that tenant. Then use the Cloud Shell hand-off (`fabric-adls-poc/CLOUD_SHELL_START.md`). A service principal would need the tenant admins and an approval.
- **Your rights:** Claude acts with your rights in that tenant. Nothing more.
