# Runs run-e2e.sh as a linux/amd64 client, regardless of what OS drives
# run-e2e.ps1. Exists because e2e.test itself is picked by --kubernetes-version
# for the *cluster's* OS/arch, but the harness's own client-side test code
# runs as whatever OS built the e2e.test/ginkgo binaries requested here - and
# some of the upstream volume test helpers build in-container exec paths with
# the client's native path separator. A windows/amd64 client sends a Linux pod
# `test -d \opt\0` instead of `test -d /opt/0`, which fails every time. A
# linux/amd64 client (this image) doesn't have that problem, whichever OS runs
# `docker run`.
#
# bash, tar and sha512sum are already in the base image; only curl (and the CA
# bundle it needs for the https:// downloads in run-e2e.sh) has to be added.
FROM debian:12-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
    && rm -rf /var/lib/apt/lists/*
