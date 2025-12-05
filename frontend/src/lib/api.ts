import axios from 'axios';

const API_URL = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:4001/api/adherence';

export const apiClient = axios.create({
  baseURL: API_URL,
  headers: {
    'Content-Type': 'application/json',
  },
  withCredentials: true,
});

// Request interceptor for adding auth tokens
apiClient.interceptors.request.use(
  (config) => {
    // TODO: Add JWT token from cookies/localStorage when auth is implemented
    // const token = Cookies.get('token');
    // if (token) {
    //   config.headers.Authorization = `Bearer ${token}`;
    // }
    return config;
  },
  (error) => {
    return Promise.reject(error);
  }
);

// Response interceptor for error handling
apiClient.interceptors.response.use(
  (response) => response,
  (error) => {
    if (error.response?.status === 401) {
      // TODO: Handle unauthorized - redirect to login
      console.error('Unauthorized - redirect to login');
    }
    return Promise.reject(error);
  }
);

export default apiClient;

