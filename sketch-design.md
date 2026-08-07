* agent written in .net running as a domain service account - needed so we can
  grant access to the agent to execute remotely on the failover cluster and
  other hyper-v nodes
* agent is installed on CSV, runs as failover cluste role with a dedicated IP,
  dns name, runs once per cluster
* all kubernetes nodes where each instance of the csi driver runs must reside on
  the same failover cluster. to support a k8s cluster running across multiple
  failover clusters, use multiple instances of the csi driver
* active directory and dns must be fully reliable without kubernetes running -
  this csi driver requires domain authentication and dns to operate, contrary to
  previous designs
* csi controller and nodes talk to the one agent instance running at one dns
  address using a service account for authentication
* agent uses one or more (different per hyper-v host) https certs for host
  authentication - csi driver can be configured with thumbprint or custom ca as
  needed
* all kubernetes nodes must be clustered - this is to prevent the VM
  configuration from becoming unreachable so we can't detatch the disk to mount
  it elsewhere
* all csi PVs must be on the CSV - byproduct of the previous requirement
* no winrm from linux to windows. if winrm/dcom to be used, it must be initiated
  from the .net agent as the service account, prefer local windows apis
* agent service account must be admin on all hyper-v nodes
* use windows apis as much as possible, powershell in .net last resort.
* in-cluster components written in go
* scalability - might not be ideal at first, overal intent is for "eventual
  consistency" at first but we may need to implement full serialization in the
  agent at first, the agent may be down momentarily for failover, etc. but
  kubernetes should keep retrying, and it'll get there eventually
  