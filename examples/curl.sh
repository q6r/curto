#!/usr/bin/env bash
# A guided tour of the curto API with curl. It needs a running server and the API key it was started with.
#
#   CURTO_API_KEY=<key> ./examples/curl.sh [base-url]        # default http://127.0.0.1:5080 (or $CURTO_URL)
#
# The tour creates one link, uses it, and deletes it again. Nothing else is touched.
set -euo pipefail

BASE="${1:-${CURTO_URL:-http://127.0.0.1:5080}}"
BASE="${BASE%/}"
KEY="${CURTO_API_KEY:?Set CURTO_API_KEY to the key the server runs with}"
JSON=(-H 'Content-Type: application/json')
AUTH=(-H "X-API-Key: $KEY")
BROWSER='Mozilla/5.0 (X11; Linux x86_64; rv:125.0) Gecko/20100101 Firefox/125.0'

step() { printf '\n# %s\n' "$1"; }
show() { printf '$ %s\n' "$1"; }   # prints the command as a reader would type it (the key is never echoed)

step "Liveness needs no key"
show 'curl -s $BASE/healthz'
curl -sS "$BASE/healthz"; echo

step "Create a link with a custom slug, a title and tags"
show 'curl -s -X POST $BASE/api/links -H "X-API-Key: $CURTO_API_KEY" -H "Content-Type: application/json" \'
echo "    -d '{\"url\":\"https://example.com/blog/hello-world\",\"slug\":\"tour\",\"title\":\"Guided tour\",\"tags\":[\"blog\",\"demo\"]}'"
curl -sS -X POST "$BASE/api/links" "${AUTH[@]}" "${JSON[@]}" \
  -d '{"url":"https://example.com/blog/hello-world","slug":"tour","title":"Guided tour","tags":["blog","demo"]}'; echo

step "The same slug again is a conflict (409)"
curl -sS -X POST "$BASE/api/links" "${AUTH[@]}" "${JSON[@]}" -d '{"url":"https://example.com/","slug":"tour"}'; echo

step "Bad input is reported per field (400)"
show "curl -s -X POST \$BASE/api/links ... -d '{\"url\":\"ftp://example.com/file\",\"slug\":\"a\",\"tags\":[\"ok\",\"not ok!\"]}'"
curl -sS -X POST "$BASE/api/links" "${AUTH[@]}" "${JSON[@]}" -d '{"url":"ftp://example.com/file","slug":"a","tags":["ok","not ok!"]}'; echo

step "Update with a JSON merge patch: present fields change, null clears"
show "curl -s -X PATCH \$BASE/api/links/tour ... -d '{\"title\":\"Guided tour (edited)\",\"maxClicks\":50}'"
curl -sS -X PATCH "$BASE/api/links/tour" "${AUTH[@]}" "${JSON[@]}" -d '{"title":"Guided tour (edited)","maxClicks":50}'; echo

step "Follow the short link: 302 and a recorded click (curl does not follow redirects unless asked)"
show 'curl -s -i $BASE/tour -H "Referer: https://news.ycombinator.com/item?id=1" -H "User-Agent: Mozilla/5.0 ... Firefox/125.0"'
curl -sS -i "$BASE/tour" -H 'Referer: https://news.ycombinator.com/item?id=1' -H "User-Agent: $BROWSER" | sed -n '1p;/^Location:/p;/^Cache-Control:/p'

step "List links: filter, sort and page"
show 'curl -s "$BASE/api/links?query=tour&status=active&sort=clicks&dir=desc&limit=5" -H "X-API-Key: $CURTO_API_KEY"'
curl -sS "$BASE/api/links?query=tour&status=active&sort=clicks&dir=desc&limit=5" "${AUTH[@]}"; echo

step "QR code of the short URL (SVG, public)"
show 'curl -s "$BASE/qr/tour.svg?size=128" | head -c 100'
curl -sS "$BASE/qr/tour.svg?size=128" | head -c 100; echo

step "Statistics of the link over the last 24 hours"
show 'curl -s "$BASE/api/links/tour/stats?range=24h" -H "X-API-Key: $CURTO_API_KEY"'
sleep 1   # the click is written asynchronously, within a fraction of a second
curl -sS "$BASE/api/links/tour/stats?range=24h" "${AUTH[@]}" | sed 's/"series":\[[^]]*\],//'; echo

step "Without the key every /api call is refused (401)"
curl -sS -o /dev/null -w 'HTTP %{http_code}\n' "$BASE/api/links"

step "Delete the link and its analytics (204), then it is gone (404)"
curl -sS -o /dev/null -w 'DELETE: HTTP %{http_code}\n' -X DELETE "$BASE/api/links/tour" "${AUTH[@]}"
curl -sS -o /dev/null -w 'GET:    HTTP %{http_code}\n' "$BASE/api/links/tour" "${AUTH[@]}"
