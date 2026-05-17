#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Set up GitHub Actions secrets for the Procare Downloader iOS signing flow.

Required inputs:
  --api-key PATH                 App Store Connect AuthKey_XXXXXXXXXX.p8 file
  --issuer-id UUID               App Store Connect issuer ID
  --certificate PATH             Apple Distribution .p12 certificate
  --profile PATH                 iOS .mobileprovision distribution profile

Usually inferred:
  --api-key-id ID                App Store Connect key ID. Inferred from AuthKey_<ID>.p8 when possible.
  --repo OWNER/REPO              GitHub repository. Inferred from git origin when possible.

Optional:
  --certificate-password VALUE   Password used when exporting the .p12 certificate
  --signing-identity NAME        Code signing identity. Default in CI: Apple Distribution
  --export-method METHOD         app-store, ad-hoc, development, or enterprise. Default: app-store
  --upload-testflight true|false Trigger workflow with TestFlight upload enabled. Default: false
  --trigger                      Trigger the iOS Signed Build workflow after setting secrets
  --dry-run                      Validate and print what would happen without setting secrets
  -h, --help                     Show this help

Example:
  ./tools/ios-signing/setup-ios-signing.sh \
    --repo shalakolee/ProcareDownloader \
    --api-key ~/Downloads/AuthKey_ABC123DEFG.p8 \
    --issuer-id 11111111-2222-3333-4444-555555555555 \
    --certificate ~/Desktop/apple_distribution.p12 \
    --profile ~/Downloads/ProcareDownloader_AppStore.mobileprovision \
    --certificate-password 'p12-password' \
    --trigger
USAGE
}

die() {
  echo "error: $*" >&2
  exit 1
}

need_file() {
  local label="$1"
  local path="$2"
  [[ -n "$path" ]] || die "$label is required"
  [[ -f "$path" ]] || die "$label does not exist: $path"
}

base64_file() {
  local path="$1"
  if command -v base64 >/dev/null 2>&1; then
    base64 < "$path" | tr -d '\n'
  elif command -v openssl >/dev/null 2>&1; then
    openssl base64 -A -in "$path"
  else
    die "base64 or openssl is required"
  fi
}

infer_repo() {
  local remote
  remote="$(git config --get remote.origin.url 2>/dev/null || true)"
  [[ -n "$remote" ]] || return 0

  remote="${remote%.git}"
  if [[ "$remote" =~ github.com[:/](.+/.+)$ ]]; then
    echo "${BASH_REMATCH[1]}"
  fi
}

set_secret() {
  local name="$1"
  local value="$2"

  if [[ "$dry_run" == "true" ]]; then
    echo "would set GitHub secret: $name"
    return 0
  fi

  printf '%s' "$value" | gh secret set "$name" --repo "$repo"
}

infer_profile_details() {
  profile_name=""
  profile_uuid=""
  team_id=""
  bundle_id=""

  if [[ "$(uname -s)" != "Darwin" ]]; then
    return 0
  fi

  local tmp
  tmp="$(mktemp)"
  if ! security cms -D -i "$profile_path" > "$tmp" 2>/dev/null; then
    rm -f "$tmp"
    return 0
  fi

  profile_name="$(/usr/libexec/PlistBuddy -c 'Print :Name' "$tmp" 2>/dev/null || true)"
  profile_uuid="$(/usr/libexec/PlistBuddy -c 'Print :UUID' "$tmp" 2>/dev/null || true)"
  team_id="$(/usr/libexec/PlistBuddy -c 'Print :TeamIdentifier:0' "$tmp" 2>/dev/null || true)"
  local application_identifier
  application_identifier="$(/usr/libexec/PlistBuddy -c 'Print :Entitlements:application-identifier' "$tmp" 2>/dev/null || true)"
  rm -f "$tmp"

  if [[ -n "$team_id" && "$application_identifier" == "$team_id."* ]]; then
    bundle_id="${application_identifier#${team_id}.}"
  fi
}

repo=""
api_key_path=""
api_key_id=""
issuer_id=""
certificate_path=""
certificate_password=""
profile_path=""
signing_identity=""
export_method="app-store"
upload_testflight="false"
trigger="false"
dry_run="false"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo)
      repo="${2:-}"; shift 2 ;;
    --api-key)
      api_key_path="${2:-}"; shift 2 ;;
    --api-key-id)
      api_key_id="${2:-}"; shift 2 ;;
    --issuer-id)
      issuer_id="${2:-}"; shift 2 ;;
    --certificate)
      certificate_path="${2:-}"; shift 2 ;;
    --certificate-password)
      certificate_password="${2:-}"; shift 2 ;;
    --profile)
      profile_path="${2:-}"; shift 2 ;;
    --signing-identity)
      signing_identity="${2:-}"; shift 2 ;;
    --export-method)
      export_method="${2:-}"; shift 2 ;;
    --upload-testflight)
      upload_testflight="${2:-}"; shift 2 ;;
    --trigger)
      trigger="true"; shift ;;
    --dry-run)
      dry_run="true"; shift ;;
    -h|--help)
      usage; exit 0 ;;
    *)
      die "unknown argument: $1" ;;
  esac
done

case "$export_method" in
  app-store|ad-hoc|development|enterprise) ;;
  *) die "--export-method must be app-store, ad-hoc, development, or enterprise" ;;
esac

case "$upload_testflight" in
  true|false) ;;
  *) die "--upload-testflight must be true or false" ;;
esac

need_file "--api-key" "$api_key_path"
need_file "--certificate" "$certificate_path"
need_file "--profile" "$profile_path"
[[ -n "$issuer_id" ]] || die "--issuer-id is required"

if [[ -z "$api_key_id" ]]; then
  api_key_file="$(basename "$api_key_path")"
  if [[ "$api_key_file" =~ ^AuthKey_([^./]+)\.p8$ ]]; then
    api_key_id="${BASH_REMATCH[1]}"
  fi
fi
[[ -n "$api_key_id" ]] || die "--api-key-id is required when the key file is not named AuthKey_<KEY_ID>.p8"

if [[ -z "$repo" ]]; then
  repo="$(infer_repo)"
fi
[[ -n "$repo" ]] || die "--repo is required when git origin is not a GitHub repository"

if [[ -z "$certificate_password" ]]; then
  read -r -s -p "Password for $certificate_path: " certificate_password
  echo
fi

if [[ "$dry_run" != "true" ]]; then
  command -v gh >/dev/null 2>&1 || die "GitHub CLI is required: https://cli.github.com/"
  gh auth status --hostname github.com >/dev/null
fi

infer_profile_details

echo "Repository: $repo"
echo "API key ID: $api_key_id"
echo "Provisioning profile: ${profile_name:-unknown}"
echo "Profile UUID: ${profile_uuid:-unknown}"
echo "Team ID: ${team_id:-unknown}"
echo "Bundle ID: ${bundle_id:-unknown}"
echo "Export method: $export_method"
echo

set_secret "APP_STORE_CONNECT_KEY_ID" "$api_key_id"
set_secret "APP_STORE_CONNECT_ISSUER_ID" "$issuer_id"
set_secret "APP_STORE_CONNECT_API_KEY_BASE64" "$(base64_file "$api_key_path")"
set_secret "IOS_DISTRIBUTION_CERTIFICATE_BASE64" "$(base64_file "$certificate_path")"
set_secret "IOS_DISTRIBUTION_CERTIFICATE_PASSWORD" "$certificate_password"
set_secret "IOS_PROVISIONING_PROFILE_BASE64" "$(base64_file "$profile_path")"

if [[ -n "$signing_identity" ]]; then
  set_secret "IOS_SIGNING_CERTIFICATE_NAME" "$signing_identity"
fi

if [[ "$trigger" == "true" ]]; then
  if [[ "$dry_run" == "true" ]]; then
    echo "would trigger workflow: ios-signed-build.yml"
  else
    gh workflow run ios-signed-build.yml \
      --repo "$repo" \
      --field export_method="$export_method" \
      --field upload_testflight="$upload_testflight"
  fi
fi

echo
echo "Done. Run the GitHub Actions workflow named 'iOS Signed Build' to produce the IPA."
