SHELL := /usr/bin/env bash
.SHELLFLAGS := -euo pipefail -c

STATE_FILE := .albany.env

-include $(STATE_FILE)

PROJECT_SUFFIX := $(if $(PROJECT_SUFFIX),$(PROJECT_SUFFIX),$(shell bash -c 'echo $$RANDOM'))

LOCATION ?= westus
RESOURCE_GROUP_NAME ?= rg-albany-$(PROJECT_SUFFIX)
ACS_NAME ?= acs-albany-$(PROJECT_SUFFIX)

.PHONY: provision
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

	@ACS_CONNECTION_STRING="$$(az communication list-key --name "$(ACS_NAME)" --resource-group "$(RESOURCE_GROUP_NAME)" --query primaryConnectionString --output tsv)"

	@printf '%s\n' \
		"PROJECT_SUFFIX := $(PROJECT_SUFFIX)" \
		"RESOURCE_GROUP_NAME := $(RESOURCE_GROUP_NAME)" \
		"LOCATION := $(LOCATION)" \
		"ACS_NAME := $(ACS_NAME)" \
		"ACS_CONNECTION_STRING := ${ACS_CONNECTION_STRING}" \
		> "$(STATE_FILE)"

run:
	@[[ -f "$(STATE_FILE)" ]] || { echo "Run 'make provision' first." >&2; exit 1; }

	@pushd ./src
	@dotnet run
	@popd
