using System;
using System.Runtime.InteropServices;
using Microsoft.Deployment.WindowsInstaller;

namespace CustomActions
{
    /// <summary>
    /// Custom action to configure Windows Service ACL so only administrators can stop/start the service.
    /// Non-admin users can only query service status.
    /// </summary>
    public class CustomActions
    {
        private const string ServiceName = "AdherenceAgentService";
        
        // Windows API constants for service access rights
        private const uint SERVICE_QUERY_STATUS = 0x0004;
        private const uint SERVICE_QUERY_CONFIG = 0x0001;
        private const uint SERVICE_INTERROGATE = 0x0080;
        private const uint SERVICE_ENUMERATE_DEPENDENTS = 0x0008;
        private const uint SERVICE_START = 0x0010;
        private const uint SERVICE_STOP = 0x0020;
        private const uint SERVICE_PAUSE_CONTINUE = 0x0040;
        private const uint SERVICE_USER_DEFINED_CONTROL = 0x0100;
        private const uint SERVICE_ALL_ACCESS = 0xF01FF;
        
        // Standard access rights
        private const uint DELETE = 0x00010000;
        private const uint READ_CONTROL = 0x00020000;
        private const uint WRITE_DAC = 0x00040000;
        private const uint WRITE_OWNER = 0x00080000;
        
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr OpenSCManager(
            string? lpMachineName,
            string? lpDatabaseName,
            uint dwDesiredAccess);
        
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr OpenService(
            IntPtr hSCManager,
            string? lpServiceName,
            uint dwDesiredAccess);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceObjectSecurity(
            IntPtr hService,
            SecurityInformation dwSecurityInformation,
            IntPtr lpSecurityDescriptor,
            uint dwBufSize,
            out uint lpdwBytesNeeded);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceObjectSecurity(
            IntPtr hService,
            SecurityInformation dwSecurityInformation,
            IntPtr lpSecurityDescriptor);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSecurityDescriptorDacl(
            IntPtr pSecurityDescriptor,
            out bool bDaclPresent,
            out IntPtr pDacl,
            out bool bDaclDefaulted);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetSecurityDescriptorDacl(
            IntPtr pSecurityDescriptor,
            bool bDaclPresent,
            IntPtr pDacl,
            bool bDaclDefaulted);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int GetLengthSid(IntPtr pSid);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool InitializeSecurityDescriptor(
            IntPtr pSecurityDescriptor,
            uint dwRevision);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool InitializeAcl(
            IntPtr pAcl,
            int nAclLength,
            uint dwAclRevision);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AddAccessAllowedAce(
            IntPtr pAcl,
            uint dwAclRevision,
            uint dwAccessMask,
            IntPtr pSid);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AddAccessDeniedAce(
            IntPtr pAcl,
            uint dwAclRevision,
            uint dwAccessMask,
            IntPtr pSid);
        
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool ConvertStringSidToSid(
            string? StringSid,
            out IntPtr pSid);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetAce(
            IntPtr pAcl,
            int dwAceIndex,
            out IntPtr pAce);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int GetAclInformation(
            IntPtr pAcl,
            IntPtr pAclInformation,
            int nAclInformationLength,
            AclInformationClass dwAclInformationClass);
        
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int SetAclInformation(
            IntPtr pAcl,
            IntPtr pAclInformation,
            int nAclInformationLength,
            AclInformationClass dwAclInformationClass);
        
        private enum SecurityInformation : uint
        {
            DACL_SECURITY_INFORMATION = 0x00000004
        }
        
        private enum AclInformationClass
        {
            AclRevisionInformation = 1,
            AclSizeInformation = 2
        }
        
        private const uint SECURITY_DESCRIPTOR_REVISION = 1;
        private const uint ACL_REVISION = 2;
        
        /// <summary>
        /// WiX custom action entry point: Configure service ACL.
        /// </summary>
        [CustomAction]
        public static ActionResult ConfigureServiceACL(Session session)
        {
            session.Log("Begin ConfigureServiceACL");
            
            try
            {
                // Open Service Control Manager with full access
                const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
                IntPtr hSCManager = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
                if (hSCManager == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    session.Log($"Failed to open SCM: Error {error}");
                    return ActionResult.Failure;
                }
                
                try
                {
                    // Open the service with WRITE_DAC access to modify security
                    IntPtr hService = OpenService(hSCManager, ServiceName, WRITE_DAC);
                    if (hService == IntPtr.Zero)
                    {
                        int error = Marshal.GetLastWin32Error();
                        session.Log($"Failed to open service {ServiceName}: Error {error}");
                        return ActionResult.Failure;
                    }
                    
                    try
                    {
                        // Get current security descriptor
                        uint bytesNeeded = 0;
                        QueryServiceObjectSecurity(hService, SecurityInformation.DACL_SECURITY_INFORMATION, IntPtr.Zero, 0, out bytesNeeded);
                        
                        if (bytesNeeded == 0)
                        {
                            session.Log("Failed to query service security descriptor size");
                            return ActionResult.Failure;
                        }
                        
                        IntPtr securityDescriptor = Marshal.AllocHGlobal((int)bytesNeeded);
                        try
                        {
                            if (!QueryServiceObjectSecurity(hService, SecurityInformation.DACL_SECURITY_INFORMATION, securityDescriptor, bytesNeeded, out bytesNeeded))
                            {
                                int error = Marshal.GetLastWin32Error();
                                session.Log($"Failed to query service security: Error {error}");
                                return ActionResult.Failure;
                            }
                            
                            // Get DACL from security descriptor
                            bool daclPresent;
                            IntPtr dacl;
                            bool daclDefaulted;
                            if (!GetSecurityDescriptorDacl(securityDescriptor, out daclPresent, out dacl, out daclDefaulted))
                            {
                                int error = Marshal.GetLastWin32Error();
                                session.Log($"Failed to get DACL: Error {error}");
                                return ActionResult.Failure;
                            }
                            
                            // Create new security descriptor
                            IntPtr newSecurityDescriptor = Marshal.AllocHGlobal(1024);
                            try
                            {
                                if (!InitializeSecurityDescriptor(newSecurityDescriptor, SECURITY_DESCRIPTOR_REVISION))
                                {
                                    int error = Marshal.GetLastWin32Error();
                                    session.Log($"Failed to initialize security descriptor: Error {error}");
                                    return ActionResult.Failure;
                                }
                                
                                // Create new DACL
                                IntPtr newDacl = Marshal.AllocHGlobal(1024);
                                try
                                {
                                    if (!InitializeAcl(newDacl, 1024, ACL_REVISION))
                                    {
                                        int error = Marshal.GetLastWin32Error();
                                        session.Log($"Failed to initialize ACL: Error {error}");
                                        return ActionResult.Failure;
                                    }
                                    
                                    // Add ACE: Administrators - Full Control
                                    IntPtr adminSid;
                                    if (!ConvertStringSidToSid("S-1-5-32-544", out adminSid)) // BUILTIN\Administrators
                                    {
                                        int error = Marshal.GetLastWin32Error();
                                        session.Log($"Failed to convert Administrators SID: Error {error}");
                                        return ActionResult.Failure;
                                    }
                                    
                                    try
                                    {
                                        if (!AddAccessAllowedAce(newDacl, ACL_REVISION, SERVICE_ALL_ACCESS, adminSid))
                                        {
                                            int error = Marshal.GetLastWin32Error();
                                            session.Log($"Failed to add Administrators ACE: Error {error}");
                                            return ActionResult.Failure;
                                        }
                                    }
                                    finally
                                    {
                                        LocalFree(adminSid);
                                    }
                                    
                                    // Add ACE: SYSTEM - Full Control
                                    IntPtr systemSid;
                                    if (!ConvertStringSidToSid("S-1-5-18", out systemSid)) // NT AUTHORITY\SYSTEM
                                    {
                                        int error = Marshal.GetLastWin32Error();
                                        session.Log($"Failed to convert SYSTEM SID: Error {error}");
                                        return ActionResult.Failure;
                                    }
                                    
                                    try
                                    {
                                        if (!AddAccessAllowedAce(newDacl, ACL_REVISION, SERVICE_ALL_ACCESS, systemSid))
                                        {
                                            int error = Marshal.GetLastWin32Error();
                                            session.Log($"Failed to add SYSTEM ACE: Error {error}");
                                            return ActionResult.Failure;
                                        }
                                    }
                                    finally
                                    {
                                        LocalFree(systemSid);
                                    }
                                    
                                    // Add ACE: Authenticated Users - Query Status Only (read-only)
                                    IntPtr usersSid;
                                    if (!ConvertStringSidToSid("S-1-5-11", out usersSid)) // NT AUTHORITY\Authenticated Users
                                    {
                                        int error = Marshal.GetLastWin32Error();
                                        session.Log($"Failed to convert Authenticated Users SID: Error {error}");
                                        return ActionResult.Failure;
                                    }
                                    
                                    try
                                    {
                                        // Only allow query status, not stop/start
                                        uint readOnlyAccess = SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG | SERVICE_INTERROGATE | SERVICE_ENUMERATE_DEPENDENTS | READ_CONTROL;
                                        if (!AddAccessAllowedAce(newDacl, ACL_REVISION, readOnlyAccess, usersSid))
                                        {
                                            int error = Marshal.GetLastWin32Error();
                                            session.Log($"Failed to add Authenticated Users ACE: Error {error}");
                                            return ActionResult.Failure;
                                        }
                                    }
                                    finally
                                    {
                                        LocalFree(usersSid);
                                    }
                                    
                                    // Set DACL in security descriptor
                                    if (!SetSecurityDescriptorDacl(newSecurityDescriptor, true, newDacl, false))
                                    {
                                        int error = Marshal.GetLastWin32Error();
                                        session.Log($"Failed to set DACL: Error {error}");
                                        return ActionResult.Failure;
                                    }
                                    
                                    // Apply new security descriptor to service
                                    if (!SetServiceObjectSecurity(hService, SecurityInformation.DACL_SECURITY_INFORMATION, newSecurityDescriptor))
                                    {
                                        int error = Marshal.GetLastWin32Error();
                                        session.Log($"Failed to set service security: Error {error}");
                                        return ActionResult.Failure;
                                    }
                                    
                                    session.Log("Service ACL configured successfully");
                                    return ActionResult.Success;
                                }
                                finally
                                {
                                    Marshal.FreeHGlobal(newDacl);
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(newSecurityDescriptor);
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(securityDescriptor);
                        }
                    }
                    finally
                    {
                        CloseServiceHandle(hService);
                    }
                }
                finally
                {
                    CloseServiceHandle(hSCManager);
                }
            }
            catch (Exception ex)
            {
                session.Log($"Exception in ConfigureServiceACL: {ex.Message}");
                session.Log($"Stack trace: {ex.StackTrace}");
                return ActionResult.Failure;
            }
        }
    }
}
