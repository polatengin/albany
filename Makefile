SHELL := /usr/bin/env bash
.SHELLFLAGS := -euo pipefail -c

STATE_FILE := .albany.env
STATE_BACKUP_VARIABLE := ALBANY_STATE
TUNNEL_LOG := .localtunnel.log
TUNNEL_PID_FILE := .localtunnel.pid

-include $(STATE_FILE)

unquote = $(subst ",,$(1))

PROJECT_SUFFIX := $(if $(PROJECT_SUFFIX),$(call unquote,$(PROJECT_SUFFIX)),$(shell bash -c 'echo $$RANDOM'))
PORT := $(if $(PORT),$(call unquote,$(PORT)),8080)
LOCATION := $(if $(LOCATION),$(call unquote,$(LOCATION)),westus)
RESOURCE_GROUP_NAME := $(if $(RESOURCE_GROUP_NAME),$(call unquote,$(RESOURCE_GROUP_NAME)),rg-albany-$(PROJECT_SUFFIX))
ACS_NAME := $(if $(ACS_NAME),$(call unquote,$(ACS_NAME)),acs-albany-$(PROJECT_SUFFIX))
AI_SERVICES_NAME := $(if $(AI_SERVICES_NAME),$(call unquote,$(AI_SERVICES_NAME)),ai-albany-$(PROJECT_SUFFIX))
AI_SERVICES_CUSTOM_DOMAIN := $(if $(AI_SERVICES_CUSTOM_DOMAIN),$(call unquote,$(AI_SERVICES_CUSTOM_DOMAIN)),ai-albany-$(PROJECT_SUFFIX))
EVENT_SUBSCRIPTION_NAME := $(if $(EVENT_SUBSCRIPTION_NAME),$(call unquote,$(EVENT_SUBSCRIPTION_NAME)),acs-incoming-calls)
TUNNEL_SUBDOMAIN := $(if $(TUNNEL_SUBDOMAIN),$(call unquote,$(TUNNEL_SUBDOMAIN)),albany-$(PROJECT_SUFFIX))
CALLBACK_BASE_URL := $(if $(CALLBACK_BASE_URL),$(call unquote,$(CALLBACK_BASE_URL)),https://$(TUNNEL_SUBDOMAIN).loca.lt)
CALLBACK_ENDPOINT := $(CALLBACK_BASE_URL)/api/incoming-call
PHONE_NUMBER := $(call unquote,$(PHONE_NUMBER))

.PHONY: provision run
provision:
	@if [[ ! -f "$(STATE_FILE)" && "$${ALBANY_STATE_RESTORE_ATTEMPTED:-}" != "1" ]]; then \
		if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then \
			tmp_state="$$(mktemp)"; \
			if gh variable get "$(STATE_BACKUP_VARIABLE)" > "$$tmp_state" 2>/dev/null && [[ -s "$$tmp_state" ]]; then \
				mv "$$tmp_state" "$(STATE_FILE)"; \
				chmod 600 "$(STATE_FILE)"; \
				echo "Restored $(STATE_FILE) from GitHub repository variable $(STATE_BACKUP_VARIABLE)."; \
				ALBANY_STATE_RESTORE_ATTEMPTED=1 make provision; \
				exit "$$?"; \
			fi; \
			rm -f "$$tmp_state"; \
		else \
			echo "GitHub CLI is not available or authenticated; skipping remote state restore."; \
		fi; \
	fi
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

	@if az cognitiveservices account show --name "$(AI_SERVICES_NAME)" --resource-group "$(RESOURCE_GROUP_NAME)" >/dev/null 2>&1; then \
		echo "Using Azure AI services account $(AI_SERVICES_NAME)"; \
	else \
		az cognitiveservices account create --name "$(AI_SERVICES_NAME)" --resource-group "$(RESOURCE_GROUP_NAME)" --location "$(LOCATION)" --kind CognitiveServices --sku S0 --custom-domain "$(AI_SERVICES_CUSTOM_DOMAIN)" --yes; \
	fi

	@AI_SERVICES_ID="$$(az cognitiveservices account show --name "$(AI_SERVICES_NAME)" --resource-group "$(RESOURCE_GROUP_NAME)" --query id --output tsv)" && \
	ACS_PRINCIPAL_ID="$$(az communication update --name "$(ACS_NAME)" --resource-group "$(RESOURCE_GROUP_NAME)" --type SystemAssigned --query identity.principalId --output tsv)" && \
	if az role assignment list --assignee "$$ACS_PRINCIPAL_ID" --role "Cognitive Services User" --scope "$$AI_SERVICES_ID" --query '[0].id' --output tsv | grep -q .; then \
		echo "Using existing Cognitive Services User role assignment"; \
	else \
		az role assignment create --assignee-object-id "$$ACS_PRINCIPAL_ID" --assignee-principal-type ServicePrincipal --role "Cognitive Services User" --scope "$$AI_SERVICES_ID"; \
	fi

	@ACS_CONNECTION_STRING="$$(az communication list-key --name "$(ACS_NAME)" --resource-group "$(RESOURCE_GROUP_NAME)" --query primaryConnectionString --output tsv)" && \
	COGNITIVE_SERVICES_ENDPOINT="$$(az cognitiveservices account show --name "$(AI_SERVICES_NAME)" --resource-group "$(RESOURCE_GROUP_NAME)" --query properties.endpoint --output tsv)" && \
	printf '%s\n' \
		"export PROJECT_SUFFIX=\"$(PROJECT_SUFFIX)\"" \
		"export RESOURCE_GROUP_NAME=\"$(RESOURCE_GROUP_NAME)\"" \
		"export LOCATION=\"$(LOCATION)\"" \
		"export ACS_NAME=\"$(ACS_NAME)\"" \
		"export AI_SERVICES_NAME=\"$(AI_SERVICES_NAME)\"" \
		"export AI_SERVICES_CUSTOM_DOMAIN=\"$(AI_SERVICES_CUSTOM_DOMAIN)\"" \
		"export EVENT_SUBSCRIPTION_NAME=\"$(EVENT_SUBSCRIPTION_NAME)\"" \
		"export TUNNEL_SUBDOMAIN=\"$(TUNNEL_SUBDOMAIN)\"" \
		"export CALLBACK_BASE_URL=\"$(CALLBACK_BASE_URL)\"" \
		"export ACS_CONNECTION_STRING=\"$$ACS_CONNECTION_STRING\"" \
		"export COGNITIVE_SERVICES_ENDPOINT=\"$$COGNITIVE_SERVICES_ENDPOINT\"" \
		"export PHONE_NUMBER=\"$(PHONE_NUMBER)\"" \
		> "$(STATE_FILE)" && \
	chmod 600 "$(STATE_FILE)"

	@if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then \
		grep -E '^export (PROJECT_SUFFIX|RESOURCE_GROUP_NAME|LOCATION|ACS_NAME|AI_SERVICES_NAME|AI_SERVICES_CUSTOM_DOMAIN|EVENT_SUBSCRIPTION_NAME|TUNNEL_SUBDOMAIN|CALLBACK_BASE_URL)=' "$(STATE_FILE)" | gh variable set "$(STATE_BACKUP_VARIABLE)" >/dev/null && \
			echo "Saved reusable resource state to GitHub repository variable $(STATE_BACKUP_VARIABLE)." || \
			echo "Warning: failed to save reusable resource state to GitHub repository variable $(STATE_BACKUP_VARIABLE)." >&2; \
	else \
		echo "GitHub CLI is not available or authenticated; skipping remote state backup."; \
	fi

	@subscription_id="$$(az account show --query id -o tsv)"; \
	source_id="/subscriptions/$$subscription_id/resourceGroups/$(RESOURCE_GROUP_NAME)/providers/Microsoft.Communication/communicationServices/$(ACS_NAME)"; \
	endpoint="$(CALLBACK_ENDPOINT)"; \
	current_endpoint="$$(az eventgrid event-subscription show --name "$(EVENT_SUBSCRIPTION_NAME)" --source-resource-id "$$source_id" --query destination.endpointBaseUrl --output tsv 2>/dev/null || true)"; \
	current_state="$$(az eventgrid event-subscription show --name "$(EVENT_SUBSCRIPTION_NAME)" --source-resource-id "$$source_id" --query provisioningState --output tsv 2>/dev/null || true)"; \
	for attempt in {1..30}; do \
		[[ -z "$$current_state" || "$$current_state" == "Succeeded" || "$$current_state" == "Failed" ]] && break; \
		echo "Waiting for Event Grid subscription to leave $$current_state state..."; \
		sleep 2; \
		current_endpoint="$$(az eventgrid event-subscription show --name "$(EVENT_SUBSCRIPTION_NAME)" --source-resource-id "$$source_id" --query destination.endpointBaseUrl --output tsv 2>/dev/null || true)"; \
		current_state="$$(az eventgrid event-subscription show --name "$(EVENT_SUBSCRIPTION_NAME)" --source-resource-id "$$source_id" --query provisioningState --output tsv 2>/dev/null || true)"; \
	done; \
	[[ -z "$$current_state" || "$$current_state" == "Succeeded" || "$$current_state" == "Failed" ]] || { echo "Event Grid subscription $(EVENT_SUBSCRIPTION_NAME) is still in state '$$current_state'. Try 'make provision' again after Azure finishes the operation." >&2; exit 1; }; \
	if [[ "$$current_endpoint" == "$$endpoint" && "$$current_state" == "Succeeded" ]]; then \
		echo "Using event grid subscription $(EVENT_SUBSCRIPTION_NAME)"; \
		exit 0; \
	fi; \
	if [[ -f "$(TUNNEL_PID_FILE)" ]] && kill -0 "$$(cat "$(TUNNEL_PID_FILE)")" 2>/dev/null; then \
		kill "$$(cat "$(TUNNEL_PID_FILE)")"; \
	fi; \
	rm -f "$(TUNNEL_LOG)" "$(TUNNEL_PID_FILE)"; \
	npx localtunnel --port "$(PORT)" --subdomain "$(TUNNEL_SUBDOMAIN)" > "$(TUNNEL_LOG)" 2>&1 & \
	tunnel_pid="$$!"; \
	app_pid=""; \
	cleanup() { \
		[[ -z "$${app_pid:-}" ]] || kill "$$app_pid" 2>/dev/null || true; \
		[[ -z "$${tunnel_pid:-}" ]] || kill "$$tunnel_pid" 2>/dev/null || true; \
		rm -f "$(TUNNEL_PID_FILE)"; \
	}; \
	trap cleanup EXIT INT TERM; \
	echo "$$tunnel_pid" > "$(TUNNEL_PID_FILE)"; \
	actual_callback_base_url=""; \
	for attempt in {1..30}; do \
		actual_callback_base_url="$$(grep -Eo 'https://[^[:space:]]+' "$(TUNNEL_LOG)" 2>/dev/null | tail -n 1 || true)"; \
		[[ -n "$$actual_callback_base_url" ]] && break; \
		sleep 1; \
	done; \
	actual_callback_base_url="$${actual_callback_base_url%/}"; \
	[[ -n "$$actual_callback_base_url" ]] || { cat "$(TUNNEL_LOG)" >&2; exit 1; }; \
	[[ "$$actual_callback_base_url" == "$(CALLBACK_BASE_URL)" ]] || { echo "localtunnel returned $$actual_callback_base_url, expected $(CALLBACK_BASE_URL). Stop the process using that subdomain, then rerun." >&2; cat "$(TUNNEL_LOG)" >&2; exit 1; }; \
	source "$(STATE_FILE)"; \
	export CALLBACK_BASE_URL="$(CALLBACK_BASE_URL)"; \
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
	if [[ -n "$$current_endpoint" ]]; then \
		az eventgrid event-subscription update --name "$(EVENT_SUBSCRIPTION_NAME)" --source-resource-id "$$source_id" --endpoint "$$endpoint" --included-event-types "Microsoft.Communication.IncomingCall"; \
	else \
		az eventgrid event-subscription create --name "$(EVENT_SUBSCRIPTION_NAME)" --source-resource-id "$$source_id" --endpoint "$$endpoint" --included-event-types "Microsoft.Communication.IncomingCall"; \
	fi; \
	for attempt in {1..30}; do \
		current_state="$$(az eventgrid event-subscription show --name "$(EVENT_SUBSCRIPTION_NAME)" --source-resource-id "$$source_id" --query provisioningState --output tsv 2>/dev/null || true)"; \
		[[ "$$current_state" == "Succeeded" ]] && break; \
		[[ "$$attempt" != "30" ]] || { echo "Event Grid subscription ended in state '$$current_state'." >&2; exit 1; }; \
		echo "Waiting for Event Grid subscription provisioning..."; \
		sleep 2; \
	done; \
	echo "Event Grid subscription $(EVENT_SUBSCRIPTION_NAME) is ready."

run:
	@[[ -f "$(STATE_FILE)" ]] || { echo "Run 'make provision' first." >&2; exit 1; }

	@source "$(STATE_FILE)"; \
	if [[ -f "$(TUNNEL_PID_FILE)" ]] && kill -0 "$$(cat "$(TUNNEL_PID_FILE)")" 2>/dev/null; then \
		kill "$$(cat "$(TUNNEL_PID_FILE)")"; \
	fi; \
	rm -f "$(TUNNEL_LOG)" "$(TUNNEL_PID_FILE)"; \
	callback_base_url="$(CALLBACK_BASE_URL)"; \
	npx localtunnel --port "$(PORT)" --subdomain "$(TUNNEL_SUBDOMAIN)" > "$(TUNNEL_LOG)" 2>&1 & \
	tunnel_pid="$$!"; \
	app_pid=""; \
	cleanup() { \
		[[ -z "$${app_pid:-}" ]] || kill "$$app_pid" 2>/dev/null || true; \
		[[ -z "$${tunnel_pid:-}" ]] || kill "$$tunnel_pid" 2>/dev/null || true; \
		rm -f "$(TUNNEL_PID_FILE)"; \
	}; \
	trap cleanup EXIT INT TERM; \
	echo "$$tunnel_pid" > "$(TUNNEL_PID_FILE)"; \
	actual_callback_base_url=""; \
	for attempt in {1..30}; do \
		actual_callback_base_url="$$(grep -Eo 'https://[^[:space:]]+' "$(TUNNEL_LOG)" 2>/dev/null | tail -n 1 || true)"; \
		[[ -n "$$actual_callback_base_url" ]] && break; \
		sleep 1; \
	done; \
	actual_callback_base_url="$${actual_callback_base_url%/}"; \
	[[ -n "$$actual_callback_base_url" ]] || { cat "$(TUNNEL_LOG)" >&2; exit 1; }; \
	[[ "$$actual_callback_base_url" == "$$callback_base_url" ]] || { echo "localtunnel returned $$actual_callback_base_url, expected $$callback_base_url. Stop the process using that subdomain, then rerun." >&2; cat "$(TUNNEL_LOG)" >&2; exit 1; }; \
	CALLBACK_BASE_URL="$$callback_base_url"; \
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
	endpoint="$(CALLBACK_ENDPOINT)"; \
	current_endpoint="$$(az eventgrid event-subscription show --name "$(EVENT_SUBSCRIPTION_NAME)" --source-resource-id "$$source_id" --query destination.endpointBaseUrl --output tsv 2>/dev/null || true)"; \
	current_state="$$(az eventgrid event-subscription show --name "$(EVENT_SUBSCRIPTION_NAME)" --source-resource-id "$$source_id" --query provisioningState --output tsv 2>/dev/null || true)"; \
	[[ "$$current_endpoint" == "$$endpoint" && "$$current_state" == "Succeeded" ]] || { echo "Event Grid subscription $(EVENT_SUBSCRIPTION_NAME) is not provisioned for $$endpoint. Run 'make provision'." >&2; exit 1; }; \
	echo "Event Grid subscription $(EVENT_SUBSCRIPTION_NAME) points to $$endpoint."; \
	wait "$$app_pid"
