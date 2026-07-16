# Network Usage Monitor

GitHub stars  
GitHub forks  
License  
GitHub issues

---

## 📌 **About the Project**

**Network Usage Monitor** is a real-time, open-source application designed to track and visualize network activity for all active processes on your Windows machine. It provides a detailed dashboard with metrics such as **download/upload speeds, total data usage, active connections, and geographic locations** of remote IPs.

Whether you're a developer, sysadmin, or just a curious user, this tool helps you monitor bandwidth usage, detect unusual network activity, and optimize your system's performance.

---

## ✨ **Features**

### 🔹 **Real-Time Monitoring**

- Track **download/upload speeds** per process in real time.
- Monitor **total data usage** (bytes sent/received).
- View **active connections** (IPs, ports, protocols).

### 🔹 **Process-Specific Insights**

- Filter and sort processes by **name, data usage, or connection count**.
- Identify **bandwidth-heavy applications** (e.g., browsers, torrents, updates).
- Detect **suspicious or unauthorized connections**.

### 🔹 **Geolocation &amp; Visualization**

- Display **country flags** for remote IPs (via geolocation).
- Sort and filter connections by **country, IP, or port**.

### 🔹 **User-Friendly Interface**

- **Dark/Light mode** support.
- **Responsive design** for desktop and laptop screens.
- **Interactive tables** with sorting (ascending/descending).
- **Modal windows** for detailed process/connection info.

### 🔹 **Performance Optimizations**

- **Low CPU/GPU usage**: Efficient backend filtering to avoid system overload.
- **No false positives**: Fixed bugs that caused incorrect traffic attribution (e.g., torrent peer leaks).

---

## 🛠 **Technologies Used**


| **Category**        | **Technologies**                                                                       |
| ------------------- | -------------------------------------------------------------------------------------- |
| **Backend**         | C#, .NET, Windows API (`GetTcpTable2`, `GetUdpTable`), `System.Net.NetworkInformation` |
| **Frontend**        | HTML5, CSS3, JavaScript (ES6+), Fetch API, DOM Manipulation                            |
| **Styling**         | Custom CSS, Flexbox, CSS Variables (for themes)                                        |
| **Data Processing** | JSON, Async/Await, Array Methods (`sort`, `filter`, `map`)                             |
| **Geolocation**     | IP-API (free tier) or custom geolocation databases                                     |
| **Build Tools**     | Visual Studio (for C#), VS Code (for JS/HTML/CSS)                                      |


---

## 📥 **Installation**

### **Prerequisites**

- **Windows 10/11** (backend relies on Windows-specific APIs).
- **.NET 6.0+** (for backend).
- Modern web browser (Chrome, Edge, Firefox) for the frontend.

### **Steps**

#### 1. **Clone the Repository**

```bash
git clone https://github.com/BlackPencil-69/Network-Usage-Monitor.git
cd Network-Usage-Monitor
```

#### 2. **Backend Setup**

- Open `DashboardServer.sln` in **Visual Studio**.
- Build the solution (`Ctrl+Shift+B`).
- Run `DashboardServer.cs` (this will start the local server on `http://localhost:5000`).

#### 3. **Frontend Setup**

- Open the `index.html` file in your browser (or use a local server like `Live Server` in VS Code).
- The dashboard will automatically connect to the backend at `http://localhost:5000`.

#### 4. **Optional: Geolocation API**

- By default, the app uses a free geolocation API (rate-limited).
- For higher accuracy, replace the API endpoint in `dashboard.js` with a paid service (e.g., IP2Location, MaxMind).

---

## 🚀 **Usage**

### **Dashboard Overview**

1. **Processes Tab**
  - Lists all active processes with network activity.
  - Columns: **Name, PID, Download, Upload, Total, Connections**.
  - Click on a process to see its **detailed connections**.
2. **Connections Tab**
  - Displays all active connections (IP, Port, Protocol, Country).
  - Supports **sorting by column** (click headers to toggle).
3. **Sorting &amp; Filtering**
  - Toggle **ascending/descending** order for processes/connections.
  - Use the **search bar** to filter by process name or IP.
4. **Themes**
  - Switch between **Dark/Light mode** via the toggle in the top-right corner.

### **Screenshots**

*(Add screenshots here after publishing to GitHub)*  
Example:

```markdown
![Dashboard Light Mode](screenshots/light-mode.png)
![Dashboard Dark Mode](screenshots/dark-mode.png)
```

---

## 🔧 **Configuration**

### **Backend (`DashboardServer.cs`)**

- **Port**: Change the port in `Program.cs` if `5000` is occupied.
- **Refresh Rate**: Adjust the `UpdateInterval` (default: 1000ms) for faster/slower updates.

### **Frontend (`dashboard.js`)**

- **Geolocation API**: Replace the endpoint in `fetchCountry()` for custom geolocation.
- **Theme Colors**: Modify CSS variables in `dashboard.css` (e.g., `--bg-primary`, `--text-primary`).

---

## 🐛 **Recent Fixes &amp; Improvements**

### **🔧 Backend (`DashboardServer.cs`)**

- **Fixed Torrent Traffic Leak**:
  - **Issue**: When a process had no active connections (empty PID list), the Windows API returned **all system connections**, causing torrent peers to appear in inactive apps.
  - **Fix**: Added a check to return an **empty array** if no PIDs are active, preventing unnecessary load on the network card and CPU.

### **🌐 Frontend**

#### **Scripts (`dashboard.js`)**

- **Fixed `ReferenceError` and UI Freezing**:
  - **Issue**: `openAppModal()` tried to render `connections` before data was fetched from the server.
  - **Fix**: Moved rendering logic **inside the success block** of the async request.
- **Improved Table Layout**:
  - Increased `colspan` from `4` to `5` for service messages (e.g., "Loading", "No active connections") to accommodate the new **country flag column**.
- **Enhanced Metric Precision**:
  - Changed percentage rounding from **1 decimal place** (`.toFixed(1)`) to **2 decimal places** (`.toFixed(2)`) for micro-process statistics.
- **Bidirectional Sorting**:
  - Added `appsSortOrder` state variable (default: `"desc"`).
  - Implemented a **toggle button** (`appsSortOrderBtn`) to switch between ascending/descending order.
  - Updated `renderApps()` to sort **text (process names)** and **numeric (bytes)** values dynamically.

#### **Markup (`index.html`)**

- **Sort Button Integration**:
  - Added a button with `id="sort-order-btn"` for sorting.
  - Moved the button **outside** the `appsSortSwitch` container to avoid conflicts with the `segmentedWire` function (which manages tab states).

#### **Styling (`dashboard.css`)**

- **Sort Button Design**:
  - Created a new `.order-btn` class for the sorting toggle.
  - Added **hover effects**, **smooth transitions**, and **theme compatibility** (dark/light mode).

---

## 🤝 **Contributing**

Contributions are welcome! Here’s how you can help:

1. **Fork the repository** and create a new branch (`git checkout -b feature/your-feature`).
2. **Commit your changes** (`git commit -m "Add your feature"`).
3. **Push to the branch** (`git push origin feature/your-feature`).
4. **Open a Pull Request** and describe your changes.

### **How to Contribute**

- **Report Bugs**: Open an issue with steps to reproduce.
- **Suggest Features**: Share ideas in the [Discussions](https://github.com/BlackPencil-69/Network-Usage-Monitor/discussions) tab.
- **Improve Code**: Submit PRs for optimizations or new features.
- **Translate**: Help localize the UI to other languages.

---

## 📜 **License**

This project is licensed under the **MIT License** – see the [LICENSE](LICENSE) file for details.

---

## 🙏 **Acknowledgments**

- Inspired by tools like **GlassWire**, **NetBalancer**, and **Windows Resource Monitor**.
- Special thanks to the open-source community for libraries and APIs used.
- Shoutout to [BlackPencil-69](https://github.com/BlackPencil-69) for initiating the project.

---

## 📞 **Contact**

- **GitHub**: [BlackPencil-69](https://github.com/BlackPencil-69)
- **Issues**: [Report a Bug](https://github.com/BlackPencil-69/Network-Usage-Monitor/issues)
- **Discussions**: [Join the Discussion](https://github.com/BlackPencil-69/Network-Usage-Monitor/discussions)

---

*Made with ❤️ for the open-source community.*
