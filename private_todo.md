
* review all code that enumerates VMs on all nodes in the failover cluster
* review all code that identifies k8s nodes by hostname/role name
* consider using host integration services to "tell" the agent what node the vm is on, confirmed with smbios id
* review all code for violations of the "fail closed" rule