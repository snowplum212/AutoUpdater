using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Updater {
    public partial class AskPermission : UserControl {
        public AskPermission() {
            InitializeComponent();
        }

        private void Num1_Click(object sender, EventArgs e) {
            tbPwd.Text += "1";
        }
    }
}
