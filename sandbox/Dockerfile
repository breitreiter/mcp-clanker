# imp's execution sandbox. Built once; reused across every contract run.
# Deliberately minimal — just what a bash contract needs to build + test a
# .NET project. No network client tools (curl/wget) by intent: the
# application-layer NetworkEgressChecker catches them at the tool level,
# and the docker runtime sets --network=none so DNS / routing are off
# regardless. Package cache lives in a bind-mounted Docker volume
# (`imp-nuget`), not baked into the image, so new packages need a
# network-enabled cache-warm pass before the contract can use them.

FROM mcr.microsoft.com/dotnet/sdk:8.0

# git for worktree-aware tools (grep, scope adherence checks, etc.).
# coreutils / findutils are already in the base image; we add git only.
RUN apt-get update \
 && apt-get install -y --no-install-recommends git \
 && rm -rf /var/lib/apt/lists/*

# Contract's bind-mounted worktree lands here. imp runs every bash
# command with -w /work, so this is effectively $PWD for the contract.
WORKDIR /work

# Reasonable locale so commands like `sort` / `date` behave predictably.
ENV LANG=C.UTF-8 \
    LC_ALL=C.UTF-8

# imp always invokes this image with `bash -c "<command>"`, so there's
# no meaningful default CMD — leaving the upstream bash shell as entry
# makes manual `docker run -it imp-sandbox` investigations work too.
CMD ["/bin/bash"]
