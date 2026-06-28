SHELL := /usr/bin/env bash
.SHELLFLAGS := -euo pipefail -c

STATE_FILE := .albany.env
TUNNEL_LOG := .localtunnel.log
TUNNEL_PID_FILE := .localtunnel.pid

-include $(STATE_FILE)

PROJECT_SUFFIX := $(if $(PROJECT_SUFFIX),$(PROJECT_SUFFIX),$(shell bash -c 'echo $$RANDOM'))

PORT ?= 8080
LOCATION ?= westus
RESOURCE_GROUP_NAME ?= rg-albany-$(PROJECT_SUFFIX)
ACS_NAME ?= acs-albany-$(PROJECT_SUFFIX)
EVENT_SUBSCRIPTION_NAME ?= acs-incoming-calls

.PHONY: provision run
provision:
	@[[ ! -f "$(STATE_FILE)" ]] || echo "Loaded $(STATE_FILE)"

	@az extension add --name communication --upgrade;

	@if [[ "$$(az group exists --name "$(RESOURCE_GROUP_NAME)")" == "true" ]]; then \
		echo "Using resource group $(RESOURCE_GROUP_NAME)"; \
	else \
		az group create --name "$(RESOURCE_GROUP_NAME)" --location "$(LOCATION)"; \
	fi

	@if az communication show --name "$(ACS_NAME)" --resource-group "$(RESOURCE_GROUP_NAME)" >/dev/null 2>&1; then \
		echo "Using communication service $(ACS_NAME)"; \
	else \
		az communication create --name "$(ACS_NAME)" --resource-group "$(RESOURCE_GROUP_NAME)" --location global --data-location "UnitedStates"; \
	fi

	@ACS_CONNECTION_STRING="$$(az communication list-key --name "$(ACS_NAME)" --resource-group "$(RESOURCE_GROUP_NAME)" --query primaryConnectionString --output tsv)" && \
	printf '%s\n' \
		"export PROJECT_SUFFIX=\"$(PROJECT_SUFFIX)\"" \
		"export RESOURCE_GROUP_NAME=\"$(RESOURCE_GROUP_NAME)\"" \
		"export LOCATION=\"$(LOCATION)\"" \
		"export ACS_NAME=\"$(ACS_NAME)\"" \
		"export ACS_CONNECTION_STRING=\"$$ACS_CONNECTION_STRING\"" \
		"export PHONE_NUMBER=\"$(PHONE_NUMBER)\"" \
		> "$(STATE_FILE)"

run:
	@[[ -f "$(STATE_FILE)" ]] || { echo "Run 'make provision' first." >&2; exit 1; }

	@source "$(STATE_FILE)"; \
	if [[ -f "$(TUNNEL_PID_FILE)" ]] && kill -0 "$$(cat "$(TUNNEL_PID_FILE)")" 2>/dev/null; then \
		kill "$$(cat "$(TUNNEL_PID_FILE)")"; \
	fi; \
	rm -f "$(TUNNEL_LOG)" "$(TUNNEL_PID_FILE)"; \
	npx localtunnel --port "$(PORT)" > "$(TUNNEL_LOG)" 2>&1 & \
	tunnel_pid="$$!"; \
	app_pid=""; \
	cleanup() { \
		[[ -z "$${app_pid:-}" ]] || kill "$$app_pid" 2>/dev/null || true; \
		[[ -z "$${tunnel_pid:-}" ]] || kill "$$tunnel_pid" 2>/dev/null || true; \
		rm -f "$(TUNNEL_PID_FILE)"; \
	}; \
	trap cleanup EXIT INT TERM; \
	echo "$$tunnel_pid" > "$(TUNNEL_PID_FILE)"; \
	CALLBACK_BASE_URL=""; \
	for attempt in {1..30}; do \
		CALLBACK_BASE_URL="$$(grep -Eo 'https://[^[:space:]]+' "$(TUNNEL_LOG)" 2>/dev/null | tail -n 1 || true)"; \
		[[ -n "$$CALLBACK_BASE_URL" ]] && break; \
		sleep 1; \
	done; \
	[[ -n "$$CALLBACK_BASE_URL" ]] || { cat "$(TUNNEL_LOG)" >&2; exit 1; }; \
	if grep -q '^export CALLBACK_BASE_URL=' "$(STATE_FILE)"; then \
		sed -i "s|^export CALLBACK_BASE_URL=.*|export CALLBACK_BASE_URL=\"$$CALLBACK_BASE_URL\"|" "$(STATE_FILE)"; \
	else \
		printf '%s\n' "export CALLBACK_BASE_URL=\"$$CALLBACK_BASE_URL\"" >> "$(STATE_FILE)"; \
	fi; \
	echo "CALLBACK_BASE_URL=$$CALLBACK_BASE_URL"; \
	echo "Incoming-call webhook: $$CALLBACK_BASE_URL/api/incoming-call"; \
	export CALLBACK_BASE_URL; \
	export ASPNETCORE_URLS="$${ASPNETCORE_URLS:-http://0.0.0.0:$(PORT)}"; \
	dotnet run --project ./src/cli.csproj & \
	app_pid="$$!"; \
	for attempt in {1..30}; do \
		if ! kill -0 "$$app_pid" 2>/dev/null; then \
			wait "$$app_pid"; \
		fi; \
		curl --max-time 2 -fsS "http://localhost:$(PORT)/" >/dev/null 2>&1 && break; \
		[[ "$$attempt" != "30" ]] || { echo "App did not start on port $(PORT)." >&2; exit 1; }; \
		sleep 1; \
	done; \
	subscription_id="$$(az account show --query id -o tsv)"; \
	source_id="/subscriptions/$$subscription_id/resourceGroups/$$RESOURCE_GROUP_NAME/providers/Microsoft.Communication/communicationServices/$$ACS_NAME"; \
	endpoint="$${CALLBACK_BASE_URL%/}/api/incoming-call"; \
	if az eventgrid event-subscription show --name "$(EVENT_SUBSCRIPTION_NAME)" --source-resource-id "$$source_id" >/dev/null 2>&1; then \
		az eventgrid event-subscription update --name "$(EVENT_SUBSCRIPTION_NAME)" --source-resource-id "$$source_id" --endpoint "$$endpoint" --included-event-types "Microsoft.Communication.IncomingCall"; \
	else \
		az eventgrid event-subscription create --name "$(EVENT_SUBSCRIPTION_NAME)" --source-resource-id "$$source_id" --endpoint "$$endpoint" --included-event-types "Microsoft.Communication.IncomingCall"; \
	fi; \
	wait "$$app_pid"
