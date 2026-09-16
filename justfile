set shell := ["pwsh", "-NoLogo", "-NoProfile", "-Command"]

run:
    pwsh -NoLogo -NoProfile -File scripts/development.ps1 run

down:
    pwsh -NoLogo -NoProfile -File scripts/development.ps1 down

test:
    pwsh -NoLogo -NoProfile -File scripts/development.ps1 test

migrate-database:
    pwsh -NoLogo -NoProfile -File scripts/development.ps1 migrate-database

recreate-database *args:
    pwsh -NoLogo -NoProfile -File scripts/development.ps1 recreate-database {{args}}
