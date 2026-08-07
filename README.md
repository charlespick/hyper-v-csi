# Hyper-V CSI Driver

A small, experimental Kubernetes CSI driver for provisioning and attaching
VHDX-backed persistent volumes to Hyper-V-based nodes.

> Warning: this project is still very early and is pretty much entirely vibe
> coded. It has no finished or production-ready installer, and much of the
> implementation is still being iterated.

## Agent Installation

Installation instructions will be added later once a proper installer is built.

## Driver Installation

Helm-based installation is planned once the chart is published. When that is
available, this README will document the expected cluster deployment steps and
the required values for connecting the driver to the clustered agent.

## Core requirements

This driver is designed around the following requirements:

- Windows Server Failover Clustering
- Clustered VMs for Kubernetes nodes
- A Cluster Shared Volume (CSV) for persistent volume storage
- VHDX-backed volumes stored on the CSV
- Hyper-V hosts that can be reached by the clustered agent
- A working Hyper-V guest environment for the node side

Kubernetes clusters that span multiple Hyper-V clusters are supported by running
multiple instances of the driver only

## Hyper-V daemons for KVP

The guest nodes need the Hyper-V Linux daemons installed, including
hv_kvp_daemon, and Data Exchange must be enabled. This allows the guest to
discover its own VM identity from Hyper-V so the driver can associate the node
with the correct VM.

## Design notes

See [design.md](design.md) for the current architecture and requirements.
