SHELL := /usr/bin/env bash
.SHELLFLAGS := -euo pipefail -c

STATE_FILE := .albany.env

-include $(STATE_FILE)

RANDOM_SUFFIX := $(shell bash -c 'echo $$RANDOM')

LOCATION ?= westus
RESOURCE_GROUP_NAME ?= rg-albany-$(RANDOM_SUFFIX)
ACS_NAME ?= acs-albany-$(RANDOM_SUFFIX)

.PHONY: provision
provision:
	@az extension add --name communication --upgrade;
	@az group create --name "$(RESOURCE_GROUP_NAME)" --location "$(LOCATION)";
	@az communication create --name "$(ACS_NAME)" --resource-group "$(RESOURCE_GROUP_NAME)" --location global --data-location "UnitedStates";
	@ACS_CONNECTION_STRING="$$(az communication list-key --name "$(ACS_NAME)" --resource-group "$(RESOURCE_GROUP_NAME)" --query primaryConnectionString --output tsv)"

	@printf '%s\n' \
		"PROJECT_SUFFIX := $(RANDOM_SUFFIX)" \
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
