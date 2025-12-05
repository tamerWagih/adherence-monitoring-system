/**
 * API Client for Adherence Admin Frontend
 * 
 * Connects to Adherence Backend Service API.
 * Base URL: Configured via NEXT_PUBLIC_API_URL environment variable
 */

const API_BASE_URL = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:4001/api/adherence';

/**
 * Get authentication token from storage
 * TODO: Implement proper JWT token storage/retrieval in Week 5
 */
function getAuthToken(): string | null {
  // Placeholder: Will be implemented with proper JWT storage in Week 5
  if (typeof window !== 'undefined') {
    return localStorage.getItem('admin_token');
  }
  return null;
}

/**
 * Make authenticated API request
 */
async function apiRequest<T>(
  endpoint: string,
  options: RequestInit = {},
): Promise<T> {
  const token = getAuthToken();
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...(options.headers as Record<string, string>),
  };

  if (token) {
    headers['Authorization'] = `Bearer ${token}`;
  }

  const response = await fetch(`${API_BASE_URL}${endpoint}`, {
    ...options,
    headers,
  });

  if (!response.ok) {
    const error = await response.json().catch(() => ({ message: 'Unknown error' }));
    throw new Error(error.message || `HTTP ${response.status}`);
  }

  return response.json();
}

/**
 * Device Management API
 */
export const devicesApi = {
  /**
   * List all workstations
   */
  list: async (params?: {
    status?: 'ACTIVE' | 'INACTIVE' | 'ALL';
    employee_id?: string;
    department?: string;
    page?: number;
    limit?: number;
  }) => {
    const queryParams = new URLSearchParams();
    if (params) {
      Object.entries(params).forEach(([key, value]) => {
        if (value !== undefined) {
          queryParams.append(key, String(value));
        }
      });
    }
    const query = queryParams.toString();
    return apiRequest(`/admin/workstations${query ? `?${query}` : ''}`);
  },

  /**
   * Get registration status
   */
  getStatus: async (params?: {
    department?: string;
    registration_status?: string;
    page?: number;
    limit?: number;
  }) => {
    const queryParams = new URLSearchParams();
    if (params) {
      Object.entries(params).forEach(([key, value]) => {
        if (value !== undefined) {
          queryParams.append(key, String(value));
        }
      });
    }
    const query = queryParams.toString();
    return apiRequest(`/admin/workstations/status${query ? `?${query}` : ''}`);
  },

  /**
   * Register new workstation
   */
  register: async (data: {
    employee_id: string;
    workstation_name?: string;
    os_version?: string;
    agent_version?: string;
    notes?: string;
  }) => {
    return apiRequest('/admin/workstations/register', {
      method: 'POST',
      body: JSON.stringify(data),
    });
  },

  /**
   * Revoke workstation
   */
  revoke: async (workstationId: string, reason?: string) => {
    return apiRequest(`/admin/workstations/${workstationId}/revoke`, {
      method: 'POST',
      body: JSON.stringify({ reason }),
    });
  },
};

/**
 * System Health API
 */
export const healthApi = {
  /**
   * Get system health status
   */
  getSystemHealth: async () => {
    return apiRequest('/admin/health');
  },
};

/**
 * Agent Sync Status API
 */
export const agentsApi = {
  /**
   * Get agent sync status
   */
  getSyncStatus: async (params?: {
    employee_id?: string;
    department?: string;
    sync_status?: string;
    page?: number;
    limit?: number;
  }) => {
    const queryParams = new URLSearchParams();
    if (params) {
      Object.entries(params).forEach(([key, value]) => {
        if (value !== undefined) {
          queryParams.append(key, String(value));
        }
      });
    }
    const query = queryParams.toString();
    return apiRequest(`/admin/agents/sync-status${query ? `?${query}` : ''}`);
  },
};

/**
 * Configuration API
 */
export const configApi = {
  /**
   * Get application classifications
   */
  getApplicationClassifications: async () => {
    return apiRequest('/admin/config/applications');
  },

  /**
   * Create application classification
   */
  createApplicationClassification: async (data: {
    name_pattern?: string;
    path_pattern?: string;
    window_title_pattern?: string;
    classification: 'WORK' | 'NON_WORK' | 'NEUTRAL';
    priority?: number;
  }) => {
    return apiRequest('/admin/config/applications', {
      method: 'POST',
      body: JSON.stringify(data),
    });
  },
};

