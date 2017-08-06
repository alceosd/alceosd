﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Text.RegularExpressions;

using System.IO;
using System.IO.Ports;
using System.Globalization;

using AlceOSD;
using MavLink;

namespace AlceOSD_updater
{
    public partial class AlceOSDconfigForm : Form
    {
        public AlceOSDconfigForm()
        {
            InitializeComponent();
        }

        private struct Canvas
        {
            public byte[] bitmap;
            public int width;
            public int height;
            public int x0;
            public int y0;

            public bool shown;
        }

        Dictionary<string, Canvas> ca = new Dictionary<string,Canvas>();
        Dictionary<string, int> widget_ids = new Dictionary<string, int>();

        int xsize = 0, ysize = 0;

        Dictionary<string, Dictionary<string, double>> widgets = new Dictionary<string, Dictionary<string, double>>();
        Dictionary<string, string> widget_name_map = new Dictionary<string, string>() {
            {"Altitude", "ALTITUD"},
            {"Alarms", "ALARMS"},
            {"Battery", "BATTERY"},
            {"Console", "CONSOLE"},
            {"Compass", "COMPASS"},
            {"Flight Info", "FLTINFO"},
            {"Flight Mode", "FLTMODE"},
            {"StOrM32 gimbal", "GIMBAL"},
            {"GPS info", "GPSINFO"},
            {"Home info", "HOMEINF"},
            {"Artificial Horizon", "HORIZON"},
            {"Radar", "RADAR"},
            {"RC Channels", "RCCHAN"},
            {"RSSI", "RSSI"},
            {"Sonar", "SONAR"},
            {"Speed", "SPEED"},
            {"Temperature", "TEMPER"},
            {"Throttle", "THROTTL"},
            {"Variometer", "VARIO"},
            {"Video levels", "VIDLVL"},
            {"Video profile", "VIDPRF"},
            {"Wind info", "WINDINF"},
        };

        string def_filename = "alceosd_config.txt";
        bool file_opened = false;

        string hw_rev = "";
        string fw_version = "";
        
        Comm comPort = new Comm();

        UserSettings settings;

        /* ********************************************************************************* */
        /* ********************************************************************************* */
        /* flasher stuff */
        UInt32 PAGE_SIZE = 0x400;
        Dictionary<UInt32, UInt32[]> pages = new Dictionary<UInt32, UInt32[]>();

        private void write_mem(UInt32 addr, UInt32 value)
        {
            UInt32 page = addr & ~((PAGE_SIZE << 1) - 1);
            if (!pages.ContainsKey(page))
            {
                pages[page] = new UInt32[PAGE_SIZE];
                for (int i = 0; i < PAGE_SIZE; i++)
                {
                    pages[page][i] = 0xffffff;
                }
            }
            int index = (int)(addr - page) >> 1;
            pages[page][index] = value;
        }

        private void do_flash(string version)
        {
            if ((version == "0.1") || (version == "0.2"))
            {
                /* ihex flasher */
                try
                {
                    using (StreamReader sr = new StreamReader(ofd_fwfile.OpenFile()))
                    {
                        string fw = sr.ReadToEnd();
                        string[] lines = fw.Split('\n');
                        int total = lines.Length;
                        string line = "";
                        string lineType = "";
                        char ret = ' ';
                        int p = 0, prev_p = -1;

                        pb.Value = 0;
                        for (int i = 0; i < total; i++)
                        {
                            p = ((i + 1) * 100 / total);
                            if (p != prev_p)
                            {
                                pb.Value = prev_p = p;
                                txt_log.AppendText("\n" + p.ToString() + "%\n");
                                Application.DoEvents();
                            }
                            line = lines[i];
                            if (line.Length < 9)
                                continue;
                            comPort.Write(line + "\n");
                            bool err = false;
                            if (version == "0.1")
                            {
                                System.Threading.Thread.Sleep(30);
                                lineType = line.Substring(7, 2);
                                if ((lineType == "02") || (lineType == "04"))
                                {
                                    continue;
                                }
                                int timeout = 0;
                                while (comPort.BytesToRead < 1)
                                {
                                    System.Threading.Thread.Sleep(1);
                                    if (timeout++ > 500)
                                    {
                                        MessageBox.Show("Timeout flashing firmware!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                        return;
                                    }
                                }
                                //txt_log.AppendText(" ");
                                while (comPort.BytesToRead > 0)
                                {
                                    ret = (char)comPort.ReadChar();
                                    //txt_log.AppendText(ret.ToString());
                                    if ((ret == '2') || (ret == '3') || (ret == 'x'))
                                    {
                                        err = true;
                                        txt_log.AppendText(ret.ToString());
                                    }
                                    if (ret == 'e')
                                    {
                                        System.Threading.Thread.Sleep(100);
                                    }
                                }
                            }
                            else
                            {
                                bool next = false;
                                while (next == false)
                                {
                                    int timeout = 0;
                                    while (comPort.BytesToRead < 1)
                                    {
                                        System.Threading.Thread.Sleep(1);
                                        if (timeout++ > 500)
                                        {
                                            MessageBox.Show("Timeout flashing firmware!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                            return;
                                        }
                                    }

                                    ret = (char)comPort.ReadChar();
                                    //txt_log.AppendText(ret.ToString());

                                    if ((ret == '2') || (ret == '3') || (ret == 'x'))
                                    {
                                        err = true;
                                        next = true;
                                        txt_log.AppendText(ret.ToString());
                                    }
                                    if (ret == 'e')
                                    {
                                        System.Threading.Thread.Sleep(100);
                                    }
                                    else if (ret == '.')
                                    {
                                        next = true;
                                    }
                                }
                            }
                            if (err)
                            {
                                MessageBox.Show("Error flashing firmware!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                //txt_log.AppendText("\n\nError on line: " + line + "\n");
                                return;
                            }
                        }
                        txt_log.AppendText("\nDone!\n");
                    }
                }
                catch
                {
                    MessageBox.Show("Error flashing firmware!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    txt_log.AppendText("Error flashing firmware!\n");
                }
            }
            else
            {
                /* 0v3+ - bin flasher */
                pages.Clear();

                string[] lines;
                using (StreamReader sr = new StreamReader(ofd_fwfile.OpenFile()))
                {
                    string fw = "";
                    fw = sr.ReadToEnd();
                    lines = fw.Split('\n');
                }

                txt_log.AppendText("Parsing ihex...\n");

                int total = lines.Length;
                string line, data;
                UInt32 a, t, l, addr = 0, e_addr = 0;
                Byte crc, ccrc;

                pb.Value = 0;
                for (int i = 0; i < total; i++)
                {
                    line = lines[i].Trim();
                    if (!line.StartsWith(":"))
                        continue;

                    l = Convert.ToUInt32(line.Substring(1, 2), 16);
                    a = Convert.ToUInt32(line.Substring(3, 4), 16);
                    t = Convert.ToUInt32(line.Substring(7, 2), 16);

                    data = line.Substring(9, (int)l * 2);
                    crc = Convert.ToByte(line.Substring(line.Length - 2, 2), 16);
                    ccrc = 0;
                    for (int j = 0; j < 5 + l; j++)
                        ccrc += Convert.ToByte(line.Substring(1 + 2 * j, 2), 16);

                    if (ccrc != 0)
                    {
                        txt_log.AppendText("CRC failed at line " + i + "\nAborting...\n");
                        return;
                    }

                    if (t == 0)
                    {
                        addr = (a + e_addr) >> 1;
                        for (int k = 0; k < (data.Length / 2) / 4; k++)
                        {
                            string word = data.Substring(k * 8, 8);
                            UInt32 value;

                            value = Convert.ToUInt32(word.Substring(4, 2), 16) << 16;
                            value |= Convert.ToUInt32(word.Substring(2, 2), 16) << 8;
                            value |= Convert.ToUInt32(word.Substring(0, 2), 16);
                            write_mem(addr + (uint)k * 2, value);
                        }
                    }
                    else if (t == 1)
                    {
                        txt_log.AppendText("Finished parsing ihex file.\n");
                        break;
                    }
                    else if (t == 2)
                    {
                        e_addr = Convert.ToUInt32(data, 16) << 8;
                    }
                    else if (t == 4)
                    {
                        e_addr = Convert.ToUInt32(data, 16) << 16;
                    }
                    else
                    {
                        txt_log.AppendText("Unknown ihex record '" + t + "'. Skipping...\n");
                    }
                }
                total = pages.Keys.Count();
                txt_log.AppendText("total_pages: " + total.ToString() + "\n");
                txt_log.AppendText("Flashing...\n");

                int c;
                byte[] iword = new byte[3];
                int p = 0, prev_p = -1;
                int ii = 0;

                foreach (KeyValuePair<UInt32, UInt32[]> entry in pages.OrderBy(entry => entry.Key))
                {
                    txt_log.AppendText("Writing page address 0x" + entry.Key.ToString("X6") + " :: ");

                    // send page address
                    iword[2] = (byte)((entry.Key));
                    iword[1] = (byte)((entry.Key) >> 8);
                    iword[0] = (byte)((entry.Key) >> 16);
                    comPort.Write(iword, 0, 3);
                    c = comPort.ReadChar(); /* returns (p)age / (e)nd*/
                    txt_log.AppendText("p=" + Convert.ToChar(c));

                    /* send page data */
                    c = comPort.ReadChar(); /* receive (s)kip / (d)ata */
                    txt_log.AppendText(" d/s=" + Convert.ToChar(c));
                    if (Convert.ToChar(c) == 's')
                    {
                        txt_log.AppendText(" protected memory, skipping...\n");
                        continue;
                    }
                    UInt16 pcrc = 0;
                    for (int k = 0; k < 0x400; k++)
                    {
                        iword[0] = (byte)((entry.Value[k]) >> 16);
                        iword[1] = (byte)((entry.Value[k]) >> 8);
                        iword[2] = (byte)((entry.Value[k]));
                        pcrc += iword[2];
                        pcrc += iword[1];
                        pcrc += iword[0];
                        comPort.Write(iword, 0, 3);
                    }

                    /* send CRC */
                    c = comPort.ReadChar(); /* request (c)rc */
                    txt_log.AppendText(" c=" + Convert.ToChar(c));
                    iword[0] = (byte)((pcrc) >> 8);
                    iword[1] = (byte)((pcrc));
                    comPort.Write(iword, 0, 2);
                    c = comPort.ReadChar();
                    txt_log.AppendText(" e=" + Convert.ToChar(c));

                    if (Convert.ToChar(c) == 'x')
                    {
                        txt_log.AppendText(" CRC ERROR!\n");
                        break;
                    }
                    c = comPort.ReadChar();
                    txt_log.AppendText(" w=" + Convert.ToChar(c) + "\n");

                    ii++;
                    p = (ii) * 100 / total;
                    if (p != prev_p)
                    {
                        pb.Value = prev_p = p;
                        Application.DoEvents();
                    }
                }
                pb.Value = 100;
                /* end */
                iword[0] = 0xff;
                iword[1] = 0xff;
                iword[2] = 0xff;
                comPort.Write(iword, 0, 3);
                txt_log.AppendText("Finished!\n");
            }
        }

        /* ********************************************************************************* */
        /* ********************************************************************************* */
        /* HW stuff */

        private void validate_hw_options()
        {
            double rev = 0.0;

            if (cb_hwrev.SelectedItem == null)
                return;

            hw_rev = cb_hwrev.SelectedItem.ToString();
            if (hw_rev.EndsWith("b"))
            {
                hw_rev = hw_rev.TrimEnd('b');
                rev = 0.1;
            }
            if (hw_rev == "")
                return;

            rev += Convert.ToInt16(hw_rev.ElementAt(hw_rev.Length - 1)) - '0';

            /* UARTS */
            gb_uart3.Visible = true;
            gb_uart4.Visible = true;
            switch (cb_hwrev.SelectedIndex)
            {
                case 0:
                    pb_uarthw.Image = AlceOSD.Properties.Resources.alceosd_hw0v1;
                    gb_uart1.Location = new Point(33, 237);
                    gb_uart2.Location = new Point(334, 120);

                    gb_uart3.Visible = false;
                    gb_uart4.Visible = false;

                    gb_uart1.Text = "UART1 (Telemetry/6pin)";
                    gb_uart2.Text = "UART2 (CON2/4+1pin)";
                    break;
                case 1:
                    pb_uarthw.Image = AlceOSD.Properties.Resources.alceosd_hw0v2;
                    gb_uart1.Location = new Point(33, 237);
                    gb_uart2.Location = new Point(310, 363);

                    gb_uart3.Visible = false;
                    gb_uart4.Visible = false;

                    gb_uart1.Text = "UART1 (Telemetry/6pin)";
                    gb_uart2.Text = "UART2 (CON2/3pin)";
                    break;
                case 2:
                    pb_uarthw.Image = AlceOSD.Properties.Resources.alceosd_hw0v3;
                    gb_uart1.Location = new Point(33, 237);
                    gb_uart2.Location = new Point(310, 115);

                    gb_uart3.Visible = false;
                    gb_uart4.Visible = false;

                    gb_uart1.Text = "UART1 (Telemetry/6pin)";
                    gb_uart2.Text = "UART2 (CON2/3pin)";
                    break;
                case 3:
                    pb_uarthw.Image = AlceOSD.Properties.Resources.alceosd_hw0v3b;
                    gb_uart1.Location = new Point(51, 168);
                    gb_uart2.Location = new Point(51, 281);

                    gb_uart3.Location = new Point(318, 55);
                    gb_uart4.Location = new Point(415, 415);

                    gb_uart1.Text = "UART1 (USART1/6pin)";
                    gb_uart2.Text = "UART2 (USART2/3pin)";
                    gb_uart3.Text = "UART3 (USART3/DF13)";
                    gb_uart4.Text = "UART4 (ICSP)";
                    break;
                case 4:
                    pb_uarthw.Image = AlceOSD.Properties.Resources.alceosd_hw0v4;
                    gb_uart1.Location = new Point(51, 168);
                    gb_uart2.Location = new Point(51, 281);

                    gb_uart3.Location = new Point(318, 55);
                    gb_uart4.Location = new Point(415, 415);

                    gb_uart1.Text = "UART1 (USART1/6pin)";
                    gb_uart2.Text = "UART2 (USART2/3pin)";
                    gb_uart3.Text = "UART3 (USART3/DF13)";
                    gb_uart4.Text = "UART4 (ICSP)";
                    break;
                case 5:
                    pb_uarthw.Image = AlceOSD.Properties.Resources.alceosd_hw0v5;
                    gb_uart1.Location = new Point(51, 168);
                    gb_uart2.Location = new Point(51, 281);

                    gb_uart3.Location = new Point(206, 49);
                    gb_uart4.Location = new Point(438, 49);

                    gb_uart1.Text = "UART1 (USART1/6pin)";
                    gb_uart2.Text = "UART2 (USART2/3pin)";
                    gb_uart3.Text = "UART3 (USART3/DF13)";
                    gb_uart4.Text = "UART4 (USB)";
                    break;
                default:
                    break;

            }

            /* VIDEO */
            if (rev < 4)
            {
                gb_vid0v4.Visible = false;
            }
            else
            {
                gb_vid0v4.Visible = true;
            }
            if (rev < 3)
            {
                gb_vsync.Visible = false;
                nud_blacklvl.Enabled = false;
                nud_graylvl.Enabled = false;
                nud_whitelvl.Enabled = true;
            }
            else
            {
                gb_vsync.Visible = true;
                nud_blacklvl.Enabled = true;
                nud_graylvl.Enabled = true;
                nud_whitelvl.Enabled = true;
            }

        }

        private string[] get_com_ports()
        {
            string[] ports = SerialPort.GetPortNames();
            return ports;

        }

        private void cb_comport_Click(object sender, EventArgs e)
        {
            cb_comport.Items.Clear();
            cb_comport.Items.AddRange(get_com_ports());
        }

        private void setup_comport()
        {
            if (comPort.IsOpen)
                comPort.Close();
            comPort.BaudRate = 115200;
            comPort.DataBits = 8;
            //comPort.StopBits = StopBits.One;
            //comPort.Parity = Parity.None;
            comPort.DtrEnable = false;
            comPort.RtsEnable = false;
            comPort.ReadTimeout = 2000;
            comPort.WriteTimeout = 2000;
        }

        private bool open_comport()
        {
            try
            {
                comPort.PortName = cb_comport.Text;
                comPort.Open();
                return true;
            }
            catch
            {
                MessageBox.Show("Error opening com port " + cb_comport.Text, "COM port error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private bool reset_board(bool flash, bool mavlink)
        {
            List<int> baudrates = new List<int> { 921600, 115200, 57600, 19200 };
            bool ready = false;
            bool flash_only = false;

            comPort.mode = Comm.COMM_MODE.Serial;
            /* check if com port is alive */
            try
            {
                comPort.WriteLine("");
            }
            catch
            {
                MessageBox.Show("Error writting to " + comPort.PortName, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                comPort.Close();
                return false;
            }

            /* check shell is active */
            Console.WriteLine("checking if shell is active");
            Console.WriteLine("find baudrate");
            foreach (int b in baudrates)
            {
                comPort.BaudRate = b;
                System.Threading.Thread.Sleep(100);
                for (int flush = 0; flush < 4; flush++)
                {
                    comPort.WriteLine("");
                    System.Threading.Thread.Sleep(100);
                }
                comPort.DiscardInBuffer();

                comPort.WriteLine("version");
                System.Threading.Thread.Sleep(100);
                string line = comPort.ReadExisting();
                Console.WriteLine("trying " + b + "... ans=" + line);
                if (line.Contains("version"))
                {
                    Console.WriteLine("got shell");
                    ready = true;
                    System.Threading.Thread.Sleep(100);
                    comPort.DiscardInBuffer();
                    break;
                }
            }

            if (ready && mavlink)
            {
                comPort.WriteLine("exit");
                ready = false;
            }

            if (mavlink)
            {
                comPort.mode = Comm.COMM_MODE.Mavlink;
                System.Threading.Thread.Sleep(100);
                Console.WriteLine("find mavkink baudrate");
                foreach (int b in baudrates)
                {
                    comPort.BaudRate = b;
                    System.Threading.Thread.Sleep(100);
                    for (int flush = 0; flush < 4; flush++)
                    {
                        comPort.WriteLine("");
                        System.Threading.Thread.Sleep(100);
                    }
                    comPort.DiscardInBuffer();
                    comPort.WriteLine("version");
                    System.Threading.Thread.Sleep(100);
                    string line = comPort.ReadExisting();
                    Console.WriteLine("trying " + b + "... ans=" + line);
                    if (line.Contains("version"))
                    {
                        Console.WriteLine("got shell");
                        ready = true;
                        System.Threading.Thread.Sleep(100);
                        comPort.DiscardInBuffer();
                        break;
                    }
                }
            }
            else if (!ready)
            {
                /* enter setup (fw0v9+ & bootloader0v5+) */
                Console.WriteLine("trying to start shell");
                foreach (int b in baudrates)
                {
                    comPort.BaudRate = b;
                    comPort.DiscardInBuffer();
                    comPort.Write("I want to enter AlceOSD setup");
                    System.Threading.Thread.Sleep(500);
                    string ans = comPort.ReadExisting();
                    Console.WriteLine("trying " + b + "... ans=" + ans);
                    if (ans.Contains("AlceOSD setup starting"))
                    {
                        comPort.Write("\n");
                        System.Threading.Thread.Sleep(500);
                        ans = comPort.ReadExisting();
                        if (ans.Contains("Exit config"))
                            flash_only = true;
                        comPort.DiscardInBuffer();
                        ready = true;
                        Console.WriteLine("shell ready");
                        break;
                    }
                }
            }

            if (ready)
            {
                if (flash_only)
                {
                    if (flash)
                    {
                        comPort.Write("#");
                        comPort.BaudRate = 115200;
                        System.Threading.Thread.Sleep(100);
                        comPort.Write("alceosd");
                        return true;
                    }
                    else
                    {
                        MessageBox.Show("FW is older than 0v11. Only flashing is possible.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                }
                Console.WriteLine("trying to get version");
                if (!send_cmd("version"))
                    return false;
                string ans = comPort.ReadLine();
                Console.WriteLine("got: {0}", ans);
                Match m = Regex.Match(ans, @"hw(.+)\sfw(.+)");
                if (m.Success)
                {
                    hw_rev = m.Groups[1].Value;
                    fw_version = m.Groups[2].Value;
                    int rev = Convert.ToInt32(hw_rev.ElementAt(hw_rev.Length - 1)) - '0';
                    Console.WriteLine("Detected hw rev {0} ({1})", hw_rev, rev);
                    string extra = "";
                    if (hw_rev == "0v4")
                        extra = "b";
                    /* keep manual selection of 0v3b over 0v3 */
                    if (cb_hwrev.SelectedItem == null ||
                        (cb_hwrev.SelectedItem != null && cb_hwrev.SelectedItem.ToString().TrimEnd('b') != hw_rev))
                        cb_hwrev.SelectedIndex = cb_hwrev.Items.IndexOf(hw_rev + extra);
                }
                System.Threading.Thread.Sleep(100);
                comPort.DiscardInBuffer();
                if (flash)
                {
                    Console.WriteLine("starting bootloader hw" + hw_rev);
                    comPort.WriteLine("reboot");
                    if (hw_rev == "0v5")
                        comPort.BaudRate = 921600;
                    else
                        comPort.BaudRate = 115200;
                    Console.WriteLine("bootloader baudrate=" + comPort.BaudRate);
                    System.Threading.Thread.Sleep(100);
                    comPort.Write("alceosd");
                }
            }
            else
            {
                Console.WriteLine("hard-reset (only on telemetry port)");
                /* bootloader < 0v5 */
                comPort.DtrEnable = true;
                comPort.RtsEnable = true;
                System.Threading.Thread.Sleep(50);
                comPort.DtrEnable = false;
                comPort.RtsEnable = false;
                System.Threading.Thread.Sleep(50);
                comPort.DiscardInBuffer();
                if (flash)
                {
                    Console.WriteLine("starting bootloader hw" + hw_rev);
                    if (hw_rev == "0v5")
                        comPort.BaudRate = 921600;
                    else
                        comPort.BaudRate = 115200;
                    Console.WriteLine("bootloader baudrate=" + comPort.BaudRate);
                    System.Threading.Thread.Sleep(100);
                    comPort.Write("alceosd");
                    return true;
                }
                else
                {
                    return false;
                }
            }
            return true;
        }


        /* ********************************************************************************* */
        /* ********************************************************************************* */
        /* old config format stuff */

        private string[] dump_config()
        {
            List<string> config = new List<string> { };
            config.Add("AlceOSD config generated by updater tool");
            config.Add("==");
            /* serial */
            config.Add("SERIAL1_MODE = " + Convert.ToDouble(cb_mode1.SelectedIndex));
            config.Add("SERIAL1_BAUD = " + Convert.ToDouble(cb_baud1.SelectedIndex));
            config.Add("SERIAL2_MODE = " + Convert.ToDouble(cb_mode2.SelectedIndex));
            config.Add("SERIAL2_BAUD = " + Convert.ToDouble(cb_baud2.SelectedIndex));
            config.Add("SERIAL3_MODE = " + Convert.ToDouble(cb_mode3.SelectedIndex));
            config.Add("SERIAL3_BAUD = " + Convert.ToDouble(cb_baud3.SelectedIndex));
            config.Add("SERIAL4_MODE = " + Convert.ToDouble(cb_mode4.SelectedIndex));
            config.Add("SERIAL4_BAUD = " + Convert.ToDouble(cb_baud4.SelectedIndex));
            /* video */
            config.Add("VIDEO_CHMODE = " + Convert.ToDouble(cb_vswmode.SelectedIndex));
            config.Add("VIDEO_CHTIME = " + (Convert.ToDouble(nud_vswtimer.Value) / 100).ToString());
            config.Add("VIDEO_CH = " + Convert.ToDouble(cb_vswch.SelectedIndex));
            config.Add("VIDEO_CHMIN = " + Convert.ToDouble(nud_vswmin.Value));
            config.Add("VIDEO_CHMAX = " + Convert.ToDouble(nud_vswmax.Value));
            config.Add("VIDEO_WHITE = " + Convert.ToDouble(nud_whitelvl.Value));
            config.Add("VIDEO_GRAY = " + Convert.ToDouble(nud_graylvl.Value));
            config.Add("VIDEO_BLACK = " + Convert.ToDouble(nud_blacklvl.Value));
            int vref = (int) nud_vsync0.Value | ((int) nud_vsync1.Value << 4);
            config.Add("VIDEO_VREF = " + Convert.ToDouble(vref));
            int vidmode = cb_vidscan.SelectedIndex;
            config.Add("VIDE0_STD = " + Convert.ToDouble(vidmode));
            config.Add("VIDE0_XSIZE = " + Convert.ToDouble(cb_xsize.SelectedIndex));
            config.Add("VIDE0_YSIZE = " + Convert.ToDouble(nud_ysize.Value));
            config.Add("VIDE0_XOFFSET = " + Convert.ToDouble(nud_xoffset.Value));
            config.Add("VIDE0_YOFFSET = " + Convert.ToDouble(nud_yoffset.Value));
            vidmode = cb_vidscan1.SelectedIndex;
            config.Add("VIDE1_STD = " + Convert.ToDouble(vidmode));
            config.Add("VIDE1_XSIZE = " + Convert.ToDouble(cb_xsize1.SelectedIndex));
            config.Add("VIDE1_YSIZE = " + Convert.ToDouble(nud_ysize1.Value));
            config.Add("VIDE1_XOFFSET = " + Convert.ToDouble(nud_xoffset1.Value));
            config.Add("VIDE1_YOFFSET = " + Convert.ToDouble(nud_yoffset1.Value));
            /* misc */
            //config.Add("HOME_LOCKING = " + Convert.ToDouble(nud_homelock.Value));
            config.Add("OSD_UNITS = " + (Convert.ToDouble(cb_units.SelectedIndex + 1)));
            /* tabs */
            config.Add("TABS_MODE = " + Convert.ToDouble(cb_tabmode.SelectedIndex));
            config.Add("TABS_TIME = " + (Convert.ToDouble(nud_tabtimer.Value) / 100).ToString());
            config.Add("TABS_CH = " + Convert.ToDouble(cb_tabch.SelectedIndex));
            config.Add("TABS_CH_MIN = " + Convert.ToDouble(nud_tabmin.Value));
            config.Add("TABS_CH_MAX = " + Convert.ToDouble(nud_tabmax.Value));
            /* mavlink */
            config.Add("MAV_UAVSYSID = " + Convert.ToDouble(nud_uavsysid.Value));
            config.Add("MAV_OSDSYSID = " + Convert.ToDouble(nud_osdsysid.Value));
            config.Add("MAV_HRTBEAT = " + Convert.ToDouble(cbx_mavhb.Checked ? 1 : 0));
            config.Add("MAV_RAWSENS = " + Convert.ToDouble(nud_streamRawSensors.Value));
            config.Add("MAV_EXTSTAT = " + Convert.ToDouble(nud_streamExtStatus.Value));
            config.Add("MAV_RCCHAN = " + Convert.ToDouble(nud_streamRcChannels.Value));
            config.Add("MAV_RAWCTRL = " + Convert.ToDouble(nud_streamRawCtrl.Value));
            config.Add("MAV_POSITION = " + Convert.ToDouble(nud_streamPosition.Value));
            config.Add("MAV_EXTRA1 = " + Convert.ToDouble(nud_streamExtra1.Value));
            config.Add("MAV_EXTRA2 = " + Convert.ToDouble(nud_streamExtra2.Value));
            config.Add("MAV_EXTRA3 = " + Convert.ToDouble(nud_streamExtra3.Value));
            /* widgets */
            foreach (KeyValuePair<string, Dictionary<string, double>> pair in widgets)
            {
                foreach (KeyValuePair<string, double> pair2 in pair.Value)
                {
                    config.Add(pair.Key + "_" + pair2.Key + " = " + Convert.ToString(pair2.Value, CultureInfo.InvariantCulture.NumberFormat));
                }
            }
            config.Add("--");
            config.Add("");
            return config.ToArray();
        }

        private void parse_config(string[] config)
        {
            List<string> vidcfg = new List<string>();
            List<string> uartcfg = new List<string>();
            List<string> mavcfg = new List<string>();
            List<string> misccfg = new List<string>();
            List<string> tabscfg = new List<string>();
            List<string> widgetscfg = new List<string>();
            string[] entry;
            string val, header;

            foreach (string value in config)
            {
                if (value.StartsWith("AlceOSD config"))
                {
                    header = value.Trim();
                    continue;
                }
                entry = value.Split('_');
                if (entry.Length > 1)
                {
                    val = String.Join("_", entry, 1, entry.Length - 1);
                    switch (entry.ElementAt(0))
                    {
                        case "VIDEO":
                        case "VIDE0":
                        case "VIDE1":
                            vidcfg.Add(value);
                            break;
                        case "SERIAL1":
                        case "SERIAL2":
                        case "SERIAL3":
                        case "SERIAL4":
                            uartcfg.Add(value);
                            break;
                        case "TABS":
                            tabscfg.Add(val);
                            break;
                        case "HOME":
                        case "OSD":
                            misccfg.Add(value);
                            break;
                        case "MAV":
                            mavcfg.Add(val);
                            break;
                        default:
                            /* widgets */
                            widgetscfg.Add(value);
                            break;
                    }
                }
            }
            try { populateVideoConfig(vidcfg); } catch { MessageBox.Show("Error parsing video config!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            try { populateUARTConfig(uartcfg); } catch { MessageBox.Show("Error parsing UART config!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            try { populateTabswitchConfig(tabscfg); } catch { MessageBox.Show("Error parsing tab switch config!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            try { populateMavlinkConfig(mavcfg); } catch { MessageBox.Show("Error parsing mavlink config!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            try { populateMiscConfig(misccfg); } catch { MessageBox.Show("Error parsing misc config!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            try { populateWidgetsConfig(widgetscfg); } catch { MessageBox.Show("Error parsing widgets config!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void openConfigToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ofd_loadcfg.FileName = def_filename;
            if (ofd_loadcfg.ShowDialog() == DialogResult.OK)
            {
                string[] config;
                def_filename = ofd_loadcfg.FileName;
                file_opened = true;
                using (StreamReader sr = new StreamReader(ofd_loadcfg.OpenFile()))
                {
                    string f = sr.ReadToEnd();
                    config = f.Split('\n');
                }
                List<string> list_config = config.ToList();
                parse_shell_config(list_config);
            }
        }

        private void populateVideoConfig(List<string> config)
        {
            string[] entry, entry2;
            string key, val, param;
            double dval;
            foreach (string value in config)
            {
                entry = value.Split('=');
                if (entry.Count() < 2)
                    continue;
                key = entry.ElementAt(0).Trim();
                entry2 = key.Split('_');
                key = entry2.ElementAt(0).Trim();
                param = entry2.ElementAt(1).Trim();
                val = entry.ElementAt(1).Trim();
                dval = Convert.ToDouble(val, CultureInfo.InvariantCulture.NumberFormat);
                //txt_log.AppendText(key + " " + param + "=" + val + "\n");
                int std = -1, xsize = -1, ysize = -1, xoffset = -1, yoffset = -1;
                int white = -1, gray = -1, black = -1;
                int chmode = -1, chtimer = -1, ch = -1, chmin = -1, chmax = -1, vref = -1;
                switch (param)
                {
                    case "STD":
                        std = Convert.ToByte(dval) & 3;
                        break;
                    case "XSIZE":
                        xsize = Convert.ToByte(dval);
                        break;
                    case "YSIZE":
                        ysize = Convert.ToInt16(dval);
                        break;
                    case "XOFFSET":
                        xoffset = Convert.ToInt16(dval);
                        break;
                    case "YOFFSET":
                        yoffset = Convert.ToInt16(dval);
                        break;
                    /* common */
                    case "CHMODE":
                        chmode = Convert.ToInt16(dval);
                        break;
                    case "CHTIME":
                        chtimer = Convert.ToInt16(dval);
                        break;
                    case "CH":
                        ch = Convert.ToInt16(dval);
                        break;
                    case "CHMIN":
                        chmin = Convert.ToInt16(dval);
                        break;
                    case "CHMAX":
                        chmax = Convert.ToInt16(dval);
                        break;
                    case "WHITE":
                        white = Convert.ToInt16(dval);
                        break;
                    case "GRAY":
                        gray = Convert.ToInt16(dval);
                        break;
                    case "BLACK":
                        black = Convert.ToInt16(dval);
                        break;
                    case "VREF":
                        vref = Convert.ToInt16(dval);
                        break;
                    default:
                        break;
                }
                try
                {
                    switch (key)
                    {
                        default:
                        case "VIDEO":
                        case "VIDE0":
                            if (chmode != -1) cb_vswmode.SelectedIndex = chmode;
                            if (chtimer != -1) nud_vswtimer.Value = chtimer * 100;
                            if (ch != -1) cb_vswch.SelectedIndex = ch;
                            if (chmin != -1) nud_vswmin.Value = chmin;
                            if (chmax != -1) nud_vswmax.Value = chmax;
                            if (std != -1) cb_vidscan.SelectedIndex = std;
                            if (xsize != -1) cb_xsize.SelectedIndex = xsize;
                            if (ysize != -1) nud_ysize.Value = ysize;
                            if (xoffset != -1) nud_xoffset.Value = xoffset;
                            if (yoffset != -1) nud_yoffset.Value = yoffset;
                            if (white != -1) nud_whitelvl.Value = white;
                            if (gray != -1) nud_graylvl.Value = gray;
                            if (black != -1) nud_blacklvl.Value = black;
                            if (vref != -1)
                            {
                                nud_vsync0.Value = vref & 0xf;
                                nud_vsync1.Value = (vref >> 4) & 0xf;
                            }
                            break;
                        case "VIDE1":
                            if (std != -1) cb_vidscan1.SelectedIndex = std;
                            if (xsize != -1) cb_xsize1.SelectedIndex = xsize;
                            if (ysize != -1) nud_ysize1.Value = ysize;
                            if (xoffset != -1) nud_xoffset1.Value = xoffset;
                            if (yoffset != -1) nud_yoffset1.Value = yoffset;
                            break;
                    }
                } catch {
                    MessageBox.Show("Error parsing video config parameter " + key + "_" + param + "!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void populateUARTConfig(List<string> config)
        {
            string[] entry, entry2;
            string key, val, param;
            double dval;
            int mode = -1, baud = -1;
            foreach (string value in config)
            {
                entry = value.Split('=');
                if (entry.Count() < 2)
                    continue;
                key = entry.ElementAt(0).Trim();
                entry2 = key.Split('_');
                key = entry2.ElementAt(0).Trim();
                param = entry2.ElementAt(1).Trim();
                val = entry.ElementAt(1).Trim();
                dval = Convert.ToDouble(val, CultureInfo.InvariantCulture.NumberFormat);
                //tb_log.AppendText(key + "=" + val + "\n");
                switch (param)
                {
                    case "MODE":
                        mode = Convert.ToByte(dval);
                        break;
                    case "BAUD":
                        baud = Convert.ToByte(dval);
                        break;
                    default:
                        break;
                }
                switch (key)
                {
                    case "SERIAL1":
                        cb_mode1.SelectedIndex = mode;
                        cb_baud1.SelectedIndex = baud;
                        break;
                    case "SERIAL2":
                        cb_mode2.SelectedIndex = mode;
                        cb_baud2.SelectedIndex = baud;
                        break;
                    case "SERIAL3":
                        cb_mode3.SelectedIndex = mode;
                        cb_baud3.SelectedIndex = baud;
                        break;
                    case "SERIAL4":
                        cb_mode4.SelectedIndex = mode;
                        cb_baud4.SelectedIndex = baud;
                        break;
                    default:
                        break;
                }
            }
        }

        private void populateTabswitchConfig(List<string> config)
        {
            string[] entry;
            string key, val;
            double dval;
            foreach (string value in config)
            {
                entry = value.Split('=');
                if (entry.Count() < 2)
                    continue;
                key = entry.ElementAt(0).Trim();
                val = entry.ElementAt(1).Trim();
                dval = Convert.ToDouble(val, CultureInfo.InvariantCulture.NumberFormat);
                //tb_log.AppendText(key + "=" + val + "\n");
                switch (key)
                {
                    case "MODE":
                        cb_tabmode.SelectedIndex = Convert.ToByte(dval);
                        break;
                    case "TIME":
                        nud_tabtimer.Value = Convert.ToByte(dval) * 100;
                        break;
                    case "CH":
                        cb_tabch.SelectedIndex = Convert.ToByte(dval);
                        break;
                    case "CH_MIN":
                        nud_tabmin.Value = Convert.ToUInt16(dval);
                        break;
                    case "CH_MAX":
                        nud_tabmax.Value = Convert.ToUInt16(dval);
                        break;
                    default:
                        break;
                }
            }
        }

        private void populateMavlinkConfig(List<string> config)
        {
            string[] entry;
            string key, val;
            double dval;
            foreach (string value in config)
            {
                entry = value.Split('=');
                if (entry.Count() < 2)
                    continue;
                key = entry.ElementAt(0).Trim();
                val = entry.ElementAt(1).Trim();
                dval = Convert.ToDouble(val, CultureInfo.InvariantCulture.NumberFormat);
                //tb_log.AppendText(key + "=" + val + "\n");
                switch (key)
                {
                    case "UAVSYSID":
                        nud_uavsysid.Value = Convert.ToByte(dval);
                        break;
                    case "OSDSYSID":
                        nud_osdsysid.Value = Convert.ToByte(dval);
                        break;
                    case "RAWSENS":
                        nud_streamRawSensors.Value = Convert.ToByte(dval);
                        break;
                    case "EXTSTAT":
                        nud_streamExtStatus.Value = Convert.ToByte(dval);
                        break;
                    case "RCCHAN":
                        nud_streamRcChannels.Value = Convert.ToByte(dval);
                        break;
                    case "RAWCTRL":
                        nud_streamRawCtrl.Value = Convert.ToByte(dval);
                        break;
                    case "POSITION":
                        nud_streamPosition.Value = Convert.ToByte(dval);
                        break;
                    case "EXTRA1":
                        nud_streamExtra1.Value = Convert.ToByte(dval);
                        break;
                    case "EXTRA2":
                        nud_streamExtra2.Value = Convert.ToByte(dval);
                        break;
                    case "EXTRA3":
                        nud_streamExtra3.Value = Convert.ToByte(dval);
                        break;
                    case "HRTBEAT":
                        cbx_mavhb.Checked = (Convert.ToByte(dval) == 1) ? true : false;
                        break;
                    default:
                        break;
                }
            }
        }

        private void populateMiscConfig(List<string> config)
        {
            string[] entry;
            string key, val;
            double dval;
            foreach (string value in config)
            {
                entry = value.Split('=');
                if (entry.Count() < 2)
                    continue;
                key = entry.ElementAt(0).Trim();
                val = entry.ElementAt(1).Trim();
                dval = Convert.ToDouble(val, CultureInfo.InvariantCulture.NumberFormat);
                //tb_log.AppendText(key + "=" + val + "\n");
                switch (key)
                {
                    case "HOME_LOCKING":
                        //nud_homelock.Value = Convert.ToByte(dval);
                        break;
                    case "OSD_UNITS":
                        cb_units.SelectedIndex = Convert.ToByte(dval) - 1;
                        break;
                    default:
                        break;
                }
            }
        }


        /* ********************************************************************************* */
        /* ********************************************************************************* */
        /* widgets stuff */

        private string get_widget_name(string name_uid)
        {
            return name_uid.Substring(0, name_uid.Length - 1);
        }
        private int get_widget_uid(string name_uid)
        {
            return (int)name_uid.ElementAt(name_uid.Length - 1) - '0';
        }
        private int get_widget_id(string name_uid)
        {
            string name = get_widget_name(name_uid);
            int id = -1;
            if (widget_ids.ContainsKey(name))
            {
                id = widget_ids[name];
            }
            Console.WriteLine("get_widget_id() nuid={0} id={1}", name_uid, id);
            return id;
        }
        private string get_widget_iduid(string name_uid)
        {
            return get_widget_id(name_uid) + "+" + get_widget_uid(name_uid);
        }

        private void submit_widget_config_pos(string name_uid)
        {
            if (!shell_active)
                return;

            if (!widgets.ContainsKey(name_uid))
                return;

            Dictionary<string, double> w = widgets[name_uid];

            string iduid = get_widget_iduid(name_uid);
            string cmd = "widgets cfg -i " + iduid;
            cmd += " -x " + w["X"];
            cmd += " -y " + w["Y"];
            cmd += " -h " + w["HJUST"];
            cmd += " -v " + w["VJUST"];
            send_cmd(cmd);
        }



        private void submit_widget_config_other(string name_uid)
        {
            if (!shell_active)
                return;

            if (!widgets.ContainsKey(name_uid))
                return;

            Dictionary<string, double> w = widgets[name_uid];
            string iduid = get_widget_iduid(name_uid);

            string cmd = "widgets cfg -i " + iduid;
            cmd += " -m " + w["MODE"];
            cmd += " -s " + w["SOURCE"];
            cmd += " -u " + w["UNITS"];
            cmd += " -a " + w["PARAM1"];
            cmd += " -b " + w["PARAM2"];
            cmd += " -c " + w["PARAM3"];
            cmd += " -d " + w["PARAM4"];
            send_cmd(cmd);
            System.Threading.Thread.Sleep(100);
            comPort.DiscardInBuffer();

            Canvas c = get_widget_canvas(name_uid);
            c.shown = true;
            ca[name_uid] = c;
            pb_osd.Invalidate();
        }



        private void populateWidgetsConfig(List<string> config)
        {

            widgets.Clear();

            string[] entry, entry2;
            string key, val, param;
            double dval;
            foreach (string value in config)
            {
                entry = value.Split('=');
                if (entry.Count() < 2)
                    continue;
                key = entry.ElementAt(0).Trim();

                entry2 = key.Split('_');

                key = entry2.ElementAt(0).Trim();
                param = entry2.ElementAt(1).Trim();

                val = entry.ElementAt(1).Trim();
                dval = Convert.ToDouble(val, CultureInfo.InvariantCulture.NumberFormat);
                //tb_log.AppendText(key + "=" + val + "\n");

                if (!widgets.ContainsKey(key))
                    widgets[key] = new Dictionary<string, double>();

                widgets[key][param] = dval;
            }

            update_lb_widgets();

            /*
            foreach (KeyValuePair<string, Dictionary<string, double>> pair in widgets)
            {

                txt_log.AppendText(pair.Key + ": ");
                foreach (KeyValuePair<string, double> pair2 in pair.Value)
                    txt_log.AppendText(pair2.Key + "=" + pair2.Value + "; ");
                txt_log.AppendText("\n");
            }
            */
        }


        private void update_lb_widgets()
        {
            lb_widgets.Items.Clear();
            foreach (KeyValuePair<string, Dictionary<string, double>> pair in widgets)
            {
                if (pair.Value["TAB"] == (double) nud_seltab.Value)
                {
                    lb_widgets.Items.Add(pair.Key);
                }
            }
            load_lb_widgets_canvas();
        }









        private void nud_seltab_ValueChanged(object sender, EventArgs e)
        {
            nud_seltab.Enabled = false;
            update_lb_widgets();
            nud_seltab.Enabled = true;
        }

        private void set_param(int idx, string text)
        {
            TextBox[] tb = { tb_wp1, tb_wp2, tb_wp3, tb_wp4 };
            Label[] lbl = { lbl_wp1, lbl_wp2, lbl_wp3, lbl_wp4 };
            lbl[idx - 1].Visible = true;
            tb[idx - 1].Visible = true;
            lbl[idx - 1].Text = text;
        }



        private void refresh_widget_config(string name_uid)
        {
            Dictionary<string, double> wcfg = widgets[name_uid];
            nud_wxpos.Value = Convert.ToDecimal(wcfg["X"]);
            nud_wypos.Value = Convert.ToDecimal(wcfg["Y"]);
            cb_wvjust.SelectedIndex = Convert.ToByte(wcfg["VJUST"]);
            cb_whjust.SelectedIndex = Convert.ToByte(wcfg["HJUST"]);
            cb_wunits.SelectedIndex = Convert.ToByte(wcfg["UNITS"]);

            tb_wp1.Text = wcfg["PARAM1"].ToString();
            tb_wp2.Text = wcfg["PARAM2"].ToString();
            tb_wp3.Text = wcfg["PARAM3"].ToString();
            tb_wp4.Text = wcfg["PARAM4"].ToString();

            if (cb_wmode.Items.Count > 0)
                cb_wmode.SelectedIndex = Convert.ToByte(wcfg["MODE"]);
            if (cb_wsource.Items.Count > 0)
                cb_wsource.SelectedIndex = Convert.ToByte(wcfg["SOURCE"]);
        }


        private string get_selected_widget()
        {
            string ret;
            if (lb_widgets.SelectedIndex != -1)
                ret = lb_widgets.SelectedItem.ToString();
            else
                ret = "";

            return ret;
        }



        bool timer_submit_allowed = true;

        private void lb_widgets_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lb_widgets.SelectedIndex < 0)
                return;

            string name_uid = lb_widgets.SelectedItem.ToString();
            string name = name_uid.Substring(0, name_uid.Length - 1);

            //get_widget_config(name_uid);

            timer_submit_allowed = false;

            pb_osd.Invalidate();

            lbl_wp1.Visible = false;
            lbl_wp2.Visible = false;
            lbl_wp3.Visible = false;
            lbl_wp4.Visible = false;
            tb_wp1.Visible = false;
            tb_wp2.Visible = false;
            tb_wp3.Visible = false;
            tb_wp4.Visible = false;

            lbl_wmode.Visible = false;
            cb_wmode.Visible = false;
            lbl_wmode.Text = "Mode";
            cb_wmode.Items.Clear();
            cb_wmode.Text = "";

            lbl_wsource.Visible = false;
            cb_wsource.Visible = false;
            cb_wsource.Items.Clear();
            cb_wsource.Text = "";

            cb_wunits.Items.Clear();
            cb_wunits.Items.Add("Default");
            cb_wunits.Items.Add("Metric");
            cb_wunits.Items.Add("Imperial");

            /* setup display */
            switch (name)
            {
                case "ALARMS":
                    lbl_wname.Text = "Alarms";
                    break;
                case "ALTITUD":
                    lbl_wname.Text = "Altitude";
                    cb_wmode.Items.Add("Dial");
                    cb_wmode.Items.Add("Text");
                    cb_wmode.Visible = true;
                    lbl_wmode.Visible = true;
                    cb_wsource.Items.Add("GPS (M.S.L.)");
                    cb_wsource.Items.Add("Home altitude");
                    cb_wsource.Items.Add("GPS2 (M.S.L.)");
                    cb_wsource.Items.Add("Terrain altitude");
                    cb_wsource.Visible = true;
                    lbl_wsource.Visible = true;
                    break;
                case "BATTERY":
                    lbl_wname.Text = "Battery, voltage and current";
                    cb_wmode.Items.Add("Mavlink voltage+current");
                    cb_wmode.Items.Add("ADC CH0");
                    cb_wmode.Items.Add("ADC CH1");
                    cb_wmode.Items.Add("ADC CH0+CH1");
                    cb_wmode.Items.Add("Calculated flight mAh");
                    lbl_wmode.Visible = true;
                    cb_wmode.Visible = true;
                    set_param(1, "ADC0 cal");
                    set_param(2, "ADC1 cal");
                    break;
                case "COMPASS":
                    lbl_wname.Text = "Compass";
                    break;
                case "CONSOLE":
                    lbl_wname.Text = "Console";
                    break;
                case "FLTINFO":
                    lbl_wname.Text = "Flight information";
                    break;
                case "FLTMODE":
                    lbl_wname.Text = "Flight mode";
                    lbl_wmode.Text = "Font";
                    lbl_wmode.Visible = true;
                    cb_wmode.Visible = true;
                    cb_wmode.Items.Add("Small");
                    cb_wmode.Items.Add("Medium");
                    cb_wmode.Items.Add("Large");
                    break;
                case "GIMBAL":
                    lbl_wname.Text = "Storm32 gimbal";
                    break;
                case "GPSINFO":
                    lbl_wname.Text = "GPS coords/status";
                    lbl_wmode.Text = "Font";
                    lbl_wmode.Visible = true;
                    lbl_wsource.Text = "Source";
                    lbl_wsource.Visible = true;
                    cb_wmode.Visible = true;
                    cb_wmode.Items.Add("Small");
                    cb_wmode.Items.Add("Medium");
                    cb_wmode.Items.Add("Large");
                    cb_wsource.Visible = true;
                    cb_wsource.Items.Add("GPS1");
                    cb_wsource.Items.Add("GPS2");
                    break;
                case "HOMEINF":
                    lbl_wname.Text = "Home information (distance, altitude, direction)";
                    break;
                case "HORIZON":
                    lbl_wname.Text = "Artificial horizon";
                    lbl_wmode.Visible = true;
                    cb_wmode.Visible = true;
                    cb_wmode.Items.Add("Default");
                    cb_wmode.Items.Add("Compass");
                    break;
                case "RADAR":
                    lbl_wname.Text = "Radar";
                    lbl_wmode.Visible = true;
                    cb_wmode.Visible = true;
                    cb_wmode.Items.Add("UAV point of view (small)");
                    cb_wmode.Items.Add("UAV point of view (large)");
                    cb_wmode.Items.Add("Pilot point of view, north up (small)");
                    cb_wmode.Items.Add("Pilot point of view, north up (large)");
                    cb_wmode.Items.Add("Pilot point of view, launch direction up (small)");
                    cb_wmode.Items.Add("Pilot point of view, launch direction up (large)");
                    cb_wmode.Items.Add("Waypoints, pilot point of view, north up (small)");
                    cb_wmode.Items.Add("Waypoints, pilot point of view, north up (large)");
                    break;
                case "RCCHAN":
                    lbl_wname.Text = "RC Channel monitor";
                    lbl_wmode.Visible = true;
                    cb_wmode.Visible = true;
                    cb_wmode.Items.Add("Numbers and Bars");
                    cb_wmode.Items.Add("Numbers only");
                    cb_wmode.Items.Add("Bars only");
                    break;
                case "RSSI":
                    lbl_wname.Text = "RSSI monitor";
                    lbl_wsource.Visible = true;
                    cb_wsource.Visible = true;
                    cb_wsource.Items.Add("Mavlink RSSI");
                    cb_wsource.Items.Add("RC Channel");
                    cb_wsource.Items.Add("ADC");
                    set_param(1, "Min value");
                    set_param(2, "Max value");
                    set_param(3, "RC(0-17=CH1-18)/ADC");
                    break;
                case "SONAR":
                    lbl_wname.Text = "Sonar";
                    break;
                case "SPEED":
                    lbl_wname.Text = "Speed";
                    lbl_wsource.Visible = true;
                    cb_wsource.Visible = true;
                    cb_wsource.Items.Add("Air speed");
                    cb_wsource.Items.Add("Ground speed");
                    lbl_wmode.Visible = true;
                    cb_wmode.Visible = true;
                    cb_wmode.Items.Add("Dial");
                    cb_wmode.Items.Add("Text");

                    cb_wunits.Items.Clear();
                    cb_wunits.Items.Add("Default");
                    cb_wunits.Items.Add("km/h");
                    cb_wunits.Items.Add("mph");
                    cb_wunits.Items.Add("m/s");
                    cb_wunits.Items.Add("f/s");
                    cb_wunits.Items.Add("kn");
                    break;
                case "TEMPER":
                    lbl_wname.Text = "Temperature";
                    break;
                case "THROTTL":
                    lbl_wname.Text = "Throttle bar";
                    break;
                case "VARIO":
                    lbl_wname.Text = "Variometer chart / vertical speed";
                    break;
                case "VIDLVL":
                    lbl_wname.Text = "Video levels (for debug)";
                    break;
                case "VIDPRF":
                    lbl_wname.Text = "Video profile";
                    lbl_wmode.Visible = true;
                    cb_wmode.Visible = true;
                    cb_wmode.Items.Add("Profile 0 (default)");
                    cb_wmode.Items.Add("Profile 1");
                    break;
                case "WINDINF":
                    lbl_wname.Text = "Wind speed";
                    cb_wunits.Items.Clear();
                    cb_wunits.Items.Add("Default");
                    cb_wunits.Items.Add("km/h");
                    cb_wunits.Items.Add("mph");
                    cb_wunits.Items.Add("m/s");
                    cb_wunits.Items.Add("f/s");
                    cb_wunits.Items.Add("kn");
                    break;
                default:
                    break;
            }

            refresh_widget_config(name_uid);
            timer_submit_allowed = true;
        }



        public static string ShowMoveToTabDialog()
        {
            Form prompt = new Form();
            prompt.Width = 300;
            prompt.Height = 150;
            prompt.FormBorderStyle = FormBorderStyle.FixedDialog;
            prompt.Text = "Destination tab";
            prompt.StartPosition = FormStartPosition.CenterScreen;
            Label textLabel = new Label() { Left = 50, Top = 20, Text = "New tab" };
            NumericUpDown nud = new NumericUpDown() { Left = 150, Top = 20, Width = 100, Maximum = 255, Minimum = 1 };
            Button confirmation = new Button() { Text = "Ok", Left = 180, Width = 70, Top = 70, DialogResult = DialogResult.OK };
            Button cancel = new Button() { Text = "Cancel", Left = 100, Width = 70, Top = 70, DialogResult = DialogResult.Cancel };
            confirmation.Click += (sender, e) => { prompt.Close(); };
            cancel.Click += (sender, e) => { prompt.Close(); };
            prompt.Controls.Add(nud);
            prompt.Controls.Add(confirmation);
            prompt.Controls.Add(cancel);
            prompt.Controls.Add(textLabel);
            prompt.AcceptButton = confirmation;

            return prompt.ShowDialog() == DialogResult.OK ? nud.Value.ToString() : "";
        }


        private void moveToTabToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string new_tab = ShowMoveToTabDialog();
            if (new_tab == "")
                return;
            string w = lb_widgets.SelectedItem.ToString();
            widgets[w]["TAB"] = Convert.ToDouble(new_tab);
            update_lb_widgets();
        }

        private void cm_widget_Opening(object sender, CancelEventArgs e)
        {
            if (lb_widgets.SelectedItems.Count == 0)
            {
                cm_widget.Items[0].Enabled = false;
                cm_widget.Items[2].Enabled = false;
            }
            else
            {
                cm_widget.Items[0].Enabled = true;
                cm_widget.Items[2].Enabled = true;
            }

        }







        private string AddNewWidgetDialog()
        {
            Form prompt = new Form();
            prompt.Width = 300;
            prompt.Height = 150;
            prompt.FormBorderStyle = FormBorderStyle.FixedDialog;
            prompt.Text = "Select Widget";
            prompt.StartPosition = FormStartPosition.CenterScreen;
            Label textLabel = new Label() { Left = 50, Top = 20, Text = "Widget" };
            ComboBox cb = new ComboBox() { Left = 150, Top = 20, Width = 100 };
            Button confirmation = new Button() { Text = "Ok", Left = 180, Width = 70, Top = 70, DialogResult = DialogResult.OK };
            Button cancel = new Button() { Text = "Cancel", Left = 100, Width = 70, Top = 70, DialogResult = DialogResult.Cancel };
            confirmation.Click += (sender, e) => { prompt.Close(); };
            cancel.Click += (sender, e) => { prompt.Close(); };
            prompt.Controls.Add(cb);
            prompt.Controls.Add(confirmation);
            prompt.Controls.Add(cancel);
            prompt.Controls.Add(textLabel);
            prompt.AcceptButton = confirmation;

            foreach (KeyValuePair<string, string> pair in widget_name_map)
            {
                cb.Items.Add(pair.Key);
            }

            cb.SelectedIndex = 0;

            string r;
            if (prompt.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    r = cb.SelectedItem.ToString();
                }
                catch
                {
                    r = "";
                }
            }
            else
            {
                r = "";
            }
            return r;
        }

        private void addWidgetToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string w = AddNewWidgetDialog();

            if (w == "")
                return;

            string wid = widget_name_map[w];

            List<string> all_wids = new List<string>();

            all_wids = widgets.Keys.ToList<string>();

            int i;
            for (i = 0; i < 10; i++) {
                if (!widgets.Keys.Contains(wid + i.ToString()))
                    break;
            }

            if (i == 10)
            {
                MessageBox.Show("Maximum number of " + wid + " widgets reached.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string name_uid = wid + i.ToString();

            Dictionary<string, double> new_wid_d = new Dictionary<string, double>() {
                {"TAB", Convert.ToDouble(nud_seltab.Value) },
                {"X", 0.0 },
                {"Y", 0.0 },
                {"HJUST", 2.0 },
                {"VJUST", 2.0 },
                {"MODE", 0.0 },
                {"SOURCE", 0.0 },
                {"UNITS", 0.0 },
                {"PARAM1", 0.0 },
                {"PARAM2", 0.0 },
                {"PARAM3", 0.0 },
                {"PARAM4", 0.0 },
            };
            widgets[name_uid] = new_wid_d;

            if (shell_active)
            {
                int id = get_widget_id(name_uid);
                send_cmd("widgets add -i " + id + " -t " + ((int)nud_seltab.Value));
                System.Threading.Thread.Sleep(100);

            }

            update_lb_widgets();
        }

        private void removeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string name_uid = lb_widgets.SelectedItem.ToString();
            DialogResult result = MessageBox.Show("Remove '" + name_uid + "' widget?",
                "Remove widget",
                MessageBoxButtons.YesNo);
            if (result == DialogResult.Yes)
            {
                widgets.Remove(name_uid);
                if (shell_active)
                {
                    int id = get_widget_id(name_uid);
                    int uid = get_widget_uid(name_uid);
                    send_cmd("widgets rm " + id + "+" + uid);
                    System.Threading.Thread.Sleep(100);

                }
                update_lb_widgets();
            }
        }


        private void save_cfg(string filename)
        {
            using (StreamWriter sw = new StreamWriter(filename))
            {
                string[] config = dump_config();

                foreach (string element in config)
                    sw.WriteLine(element);
            }
        }


        private int pos2canvas(string name, Canvas c, char dir)
        {
            if (!widgets.ContainsKey(name))
                return 0;
            Dictionary<string, double> w = widgets[name];
            int pos = (int) ((dir == 'H') ? w["X"] : w["Y"]);
            int div = (int) w[dir + "JUST"];
            int osd_size = (dir == 'H') ? xsize : ysize;
            int wid_size = (dir == 'H') ? c.width * 4 : c.height;

            if (div > 0)
                pos += (osd_size - wid_size) / div;

            return pos;
        }



        private void flag_submit(string type)
        {
            string name_uid = get_selected_widget();

            if (!timer_submit_allowed)
                return;

            if (type.Contains("P"))
            {
                timer_submit.Tag += "P";
                if (ca.ContainsKey(name_uid))
                {
                    Canvas c = ca[name_uid];
                    c.x0 = pos2canvas(name_uid, c, 'H');
                    c.y0 = pos2canvas(name_uid, c, 'V');
                    ca[name_uid] = c;
                    pb_osd.Invalidate();
                }
            }
            else if (type.Contains("O"))
            {
                timer_submit.Tag += "O";
            }

            timer_submit.Stop();
            timer_submit.Start();
        }


        private void saveAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ofd_savecfg.FileName = def_filename;
            if (ofd_savecfg.ShowDialog() != DialogResult.OK)
                return;

            save_cfg(ofd_savecfg.FileName);
            def_filename = ofd_savecfg.FileName;
            this.Text = "AlceOSD config editor - " + Path.GetFileName(def_filename);
        }

        private void saveConfigToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!file_opened)
            {
                ofd_savecfg.FileName = def_filename;
                ofd_savecfg.ShowDialog();

                if (ofd_savecfg.ShowDialog() != DialogResult.OK)
                    return;

                file_opened = true;
                def_filename = ofd_savecfg.FileName;
            }

            save_cfg(def_filename);
            this.Text = "AlceOSD Config Editor - " + Path.GetFileName(def_filename);
        }



        private void nud_wxpos_ValueChanged(object sender, EventArgs e)
        {
            if (lb_widgets.SelectedItems.Count == 0)
                return;
            string w = lb_widgets.SelectedItem.ToString();
            widgets[w]["X"] = Convert.ToDouble(nud_wxpos.Value);
            flag_submit("P");
        }

        private void nud_wypos_ValueChanged(object sender, EventArgs e)
        {
            if (lb_widgets.SelectedItems.Count == 0)
                return;
            string w = lb_widgets.SelectedItem.ToString();
            widgets[w]["Y"] = Convert.ToDouble(nud_wypos.Value);
            flag_submit("P");
        }

        private void cb_whjust_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lb_widgets.SelectedItems.Count == 0)
                return;
            string w = lb_widgets.SelectedItem.ToString();
            widgets[w]["HJUST"] = Convert.ToDouble(cb_whjust.SelectedIndex);
            flag_submit("P");
        }

        private void cb_wvjust_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lb_widgets.SelectedItems.Count == 0)
                return;
            string w = lb_widgets.SelectedItem.ToString();
            widgets[w]["VJUST"] = Convert.ToDouble(cb_wvjust.SelectedIndex);
            flag_submit("P");
        }

        private void cb_wmode_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lb_widgets.SelectedItems.Count == 0)
                return;
            string w = lb_widgets.SelectedItem.ToString();
            widgets[w]["MODE"] = Convert.ToDouble(cb_wmode.SelectedIndex);
            flag_submit("O");
        }

        private void cb_wunits_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lb_widgets.SelectedItems.Count == 0)
                return;
            string w = lb_widgets.SelectedItem.ToString();
            widgets[w]["UNITS"] = Convert.ToDouble(cb_wunits.SelectedIndex);
            flag_submit("O");
        }

        private void cb_wsource_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lb_widgets.SelectedItems.Count == 0)
                return;
            string w = lb_widgets.SelectedItem.ToString();
            widgets[w]["SOURCE"] = Convert.ToDouble(cb_wsource.SelectedIndex);
            flag_submit("O");
        }

        private void tb_wp1_TextChanged(object sender, EventArgs e)
        {
            if (lb_widgets.SelectedItems.Count == 0)
                return;
            string w = lb_widgets.SelectedItem.ToString();
            try
            {
                widgets[w]["PARAM1"] = Convert.ToDouble(tb_wp1.Text, CultureInfo.InvariantCulture.NumberFormat);
            }
            catch
            {
                widgets[w]["PARAM1"] = 0.0;
            }
            flag_submit("O");
        }

        private void tb_wp2_TextChanged(object sender, EventArgs e)
        {
            if (lb_widgets.SelectedItems.Count == 0)
                return;
            string w = lb_widgets.SelectedItem.ToString();

            try
            {
                widgets[w]["PARAM2"] = Convert.ToDouble(tb_wp2.Text, CultureInfo.InvariantCulture.NumberFormat);
            }
            catch
            {
                widgets[w]["PARAM2"] = 0.0;
            }
            flag_submit("O");
        }

        private void tb_wp3_TextChanged(object sender, EventArgs e)
        {
            if (lb_widgets.SelectedItems.Count == 0)
                return;
            string w = lb_widgets.SelectedItem.ToString();
            try
            {
                widgets[w]["PARAM3"] = Convert.ToDouble(tb_wp3.Text, CultureInfo.InvariantCulture.NumberFormat);
            }
            catch
            {
                widgets[w]["PARAM3"] = 0.0;
            }
            flag_submit("O");
        }

        private void tb_wp4_TextChanged(object sender, EventArgs e)
        {
            if (lb_widgets.SelectedItems.Count == 0)
                return;
            string w = lb_widgets.SelectedItem.ToString();
            try
            {
                widgets[w]["PARAM4"] = Convert.ToDouble(tb_wp4.Text, CultureInfo.InvariantCulture.NumberFormat);
            }
            catch
            {
                widgets[w]["PARAM4"] = 0.0;
            }
            flag_submit("O");
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void Form2_FormClosing(object sender, FormClosingEventArgs e)
        {
            DialogResult result = MessageBox.Show(
                "Save config?", "Exiting...",
                MessageBoxButtons.YesNo);
            if (result == DialogResult.Yes)
            {
                ofd_savecfg.FileName = def_filename;
                if (ofd_savecfg.ShowDialog() != DialogResult.OK)
                    return;
                save_cfg(ofd_savecfg.FileName);
            }

            settings.Save();
            comPort.Close();
        }

        private void readConfigToolStripMenuItem_Click(object sender, EventArgs e)
        {
            setup_comport();
            if (!open_comport())
                return;
            if (!reset_board(false, cbx_mavmode.Checked))
                return;

            txt_log.AppendText("Port " + comPort.PortName + " opened for config read\n");

            /* dump config */
            comPort.Write("config dump\n");

            List<string> config = new List<string> { };
            string line;
            bool started = false;
            bool finished = false;
            while (!finished)
            {
                try
                {
                    line = comPort.ReadLine();
                }
                catch
                {
                    MessageBox.Show("Error waiting for config!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    txt_log.AppendText("Error waiting for config!\n");
                    comPort.Close();
                    return;
                }


                if (line.Contains("AlceOSD config"))
                {
                    started = true;
                    txt_log.AppendText(line + '\n');
                }

                if (started)
                {
                    config.Add(line);
                    if (line.StartsWith("--"))
                        finished = true;
                }
                //System.Threading.Thread.Sleep(10);
            }
            comPort.Close();
            MessageBox.Show("Config successfully read", "Config", MessageBoxButtons.OK, MessageBoxIcon.Information);

            parse_config(config.ToArray());

        }

        private void writeConfigToolStripMenuItem_Click(object sender, EventArgs e)
        {

            string[] config;
            ofd_loadcfg.FileName = def_filename;
            if (ofd_loadcfg.ShowDialog() == DialogResult.OK)
            {
                def_filename = ofd_loadcfg.FileName;
                file_opened = true;

                this.Text = "AlceOSD Config Editor - " + Path.GetFileName(def_filename);

                using (StreamReader sr = new StreamReader(ofd_loadcfg.OpenFile()))
                {
                    string fw = "";
                    fw = sr.ReadToEnd();
                    config = fw.Split('\n');
                }
                parse_config(config);
            }




            setup_comport();
            if (!open_comport())
                return;
            if (!reset_board(false, cbx_mavmode.Checked))
                return;

            txt_log.AppendText("Port " + comPort.PortName + " opened for config upload\n");

            comPort.Write("config load\n");
            System.Threading.Thread.Sleep(100);

            config = dump_config();


            string line;
            while (comPort.BytesToRead > 0)
            {
                comPort.DiscardInBuffer();
                System.Threading.Thread.Sleep(20);
            }
            txt_log.AppendText("Writing config to board\n");

            int total = config.Length;
            int p, i = 0;
            pb.Value = 0;

            foreach (string value in config)
            {
                comPort.Write(value + '\n');
                System.Threading.Thread.Sleep(20);
                if (comPort.BytesToRead > 0)
                {
                    line = comPort.ReadLine();
                    txt_log.AppendText(".");
                    txt_log.AppendText(line + '\n');
                }

                p = (++i) * 100 / total;
                if (p != pb.Value)
                {
                    pb.Value = p;
                    Application.DoEvents();
                }

            }
            pb.Value = 100;
            comPort.Write(".\n");
            txt_log.AppendText("Done!\n");

            comPort.Write("config save\n");
            System.Threading.Thread.Sleep(500);
            comPort.Write("reboot\r");
            comPort.Close();
            MessageBox.Show("Config successfully uploaded, rebooting", "Config", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void downloadFirmwareToolStripMenuItem_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("https://github.com/ArduPilot/alceosd/releases");
        }


        private void recalculate_pb_osd_size()
        {
            int vidprf = 0;
            string[] lst = new string[lb_widgets.Items.Count];
            lb_widgets.Items.CopyTo(lst, 0);
            foreach (string name_uid in lst)
            {
                if (name_uid.Contains("VIDPRF"))
                {
                    vidprf = (int)widgets[name_uid]["MODE"];
                    break;
                }
            }

            int cb_xsize_idx = (vidprf == 0) ? cb_xsize.SelectedIndex : cb_xsize1.SelectedIndex;
            switch (cb_xsize_idx)
            {
                case 0:
                default:
                    xsize = 480;
                    break;
                case 1:
                    xsize = 480;
                    break;
                case 2:
                    xsize = 560;
                    break;
                case 3:
                    xsize = 672;
                    break;
            }
            bool pal = true;
            ysize = pal ? 260 : 210;

            string std = (vidprf == 0) ? cb_vidscan.Text : cb_vidscan1.Text;

            if (std.Contains("nterlaced"))
                ysize *= 2;

            ysize -= (int) nud_ysize.Value;


            Size s = new Size();
            s.Height = ysize + 10;
            s.Width = xsize + 10;
            pb_osd.Size = s;


            string[] keys = ca.Keys.ToArray();
            foreach (string name_uid in keys)
            {
                Canvas c = ca[name_uid];
                c.x0 = pos2canvas(name_uid, c, 'H');
                c.y0 = pos2canvas(name_uid, c, 'V');
                c.shown = lb_widgets.Items.Contains(name_uid);
                ca[name_uid] = c;
            }
        }


        private void AlceOSDconfigForm_Load(object sender, EventArgs e)
        {
            settings = new UserSettings();

            cb_comport.Text = settings.ComPort;

            cb_vswch.Items.Clear();
            cb_tabch.Items.Clear();
            for (int i = 1; i < 19; i++)
            {
                cb_vswch.Items.Add("RC" + i.ToString());
                cb_tabch.Items.Add("RC" + i.ToString());
            }
            recalculate_pb_osd_size();
        }

        private void cb_comport_SelectedIndexChanged(object sender, EventArgs e)
        {
            settings.ComPort = cb_comport.Text;
            settings.Save();
        }
        

        private void get_widget_ids()
        {
            send_cmd("widgets available 1");

            widget_ids.Clear();
            while (true) {
                string line = comPort.ReadLine();
                Console.WriteLine("get_widget_ids() {0}", line);
                if (line == "--")
                    break;
                string[] info = line.Split(',');
                widget_ids.Add(info[2].Trim(), Convert.ToInt16(info[0]));
            }
        }

        private Dictionary<string, string> get_cmd_switches(string cmd)
        {
            Dictionary<string, string> result = new Dictionary<string, string> { };

            MatchCollection mc = Regex.Matches(cmd, @"\-(.)\s*(.+?)(\s|$)");
            foreach (Match m in mc)
            {
                if (m.Success)
                {
                    //Console.WriteLine("get_cmd_switches: {0} = {1}", m.Groups[1].Value, m.Groups[2].Value);
                    string param = m.Groups[1].Value;
                    string value = m.Groups[2].Value;
                    result[param] = value;
                }
            }
            return result;
        }
        /*
        private string get_cmd_switchval(string cmd, string sw)
        {
            Match m = Regex.Match(cmd, @".+\s\-" + sw + @"\s*(.+?)(\s.+|$)");
            if (m.Success)
            {
                return m.Groups[1].Value;
                //Console.WriteLine("get_cmd_switchval: switch=-{0} value={1}", sw, m.Groups[1].Value);
            }
            else
                return "";
        }*/


        

        private void parse_shell_config(List<string> config)
        {
            foreach (string line in config)
            {
                string cmd = line.Trim();

                /* comment */
                if (cmd.StartsWith("#") || cmd == "")
                    continue;


                Dictionary<string, string> sw = get_cmd_switches(cmd);
                string mod = cmd.Split(' ')[0];

                switch (mod)
                {
                    case "uart":
                        if (!sw.ContainsKey("p"))
                        {
                            txt_log.AppendText("Error: missing port (-p) switch on cmd '" + cmd + "'");
                            continue;
                        }
                        int bauds, cli = -1;
                        if (sw.ContainsKey("b"))
                        {
                            bauds = cb_baud1.Items.IndexOf(sw["b"]);
                        }
                        else
                        {
                            bauds = cb_baud1.SelectedIndex;
                        }

                        if (sw.ContainsKey("c"))
                        {
                            foreach (string i in cb_mode1.Items)
                            {
                                if (i.ToLower() == sw["c"])
                                {
                                    sw["c"] = i;
                                    break;
                                }
                            }
                            cli = cb_mode1.Items.IndexOf(sw["c"]);
                        }
                        else
                        {
                            cli = cb_mode1.SelectedIndex;
                        }
                        //Console.WriteLine("will change port " + sw["p"] + " baud=" + bauds + " cli=" + cli);
                        switch (sw["p"])
                        {
                            case "0":
                                cb_baud1.SelectedIndex = bauds;
                                cb_mode1.SelectedIndex = cli;
                                break;
                            case "1":
                                cb_baud2.SelectedIndex = bauds;
                                cb_mode2.SelectedIndex = cli;
                                break;
                            case "2":
                                cb_baud3.SelectedIndex = bauds;
                                cb_mode3.SelectedIndex = cli;
                                break;
                            case "3":
                                cb_baud4.SelectedIndex = bauds;
                                cb_mode4.SelectedIndex = cli;
                                break;
                            default:
                                break;
                        }
                        break;
                    case "video":
                        switch (cmd.Split(' ')[1])
                        {
                            case "config":
                                if (sw.ContainsKey("w"))
                                    nud_whitelvl.Value = Convert.ToDecimal(sw["w"]);
                                if (sw.ContainsKey("g"))
                                    nud_graylvl.Value = Convert.ToDecimal(sw["g"]);
                                if (sw.ContainsKey("b"))
                                    nud_blacklvl.Value = Convert.ToDecimal(sw["b"]);
                                if (sw.ContainsKey("r"))
                                    nud_vsync0.Value = Convert.ToDecimal(sw["r"]);
                                if (sw.ContainsKey("f"))
                                    nud_vsync1.Value = Convert.ToDecimal(sw["f"]);

                                if (sw.ContainsKey("p"))
                                {
                                    switch (sw["p"])
                                    {
                                        case "0":
                                            if (sw.ContainsKey("m"))
                                                cb_vidscan.SelectedIndex = sw["m"] == "p" ? 0 : 1;
                                            if (sw.ContainsKey("x"))
                                                cb_xsize.SelectedIndex = cb_xsize.Items.IndexOf(sw["x"]);
                                            if (sw.ContainsKey("y"))
                                                nud_ysize.Value = Convert.ToInt16(sw["y"]);
                                            if (sw.ContainsKey("h"))
                                                nud_xoffset.Value = Convert.ToInt16(sw["h"]);
                                            if (sw.ContainsKey("v"))
                                                nud_yoffset.Value = Convert.ToInt16(sw["v"]);
                                            break;
                                        case "1":
                                            if (sw.ContainsKey("m"))
                                                cb_vidscan1.SelectedIndex = sw["m"] == "p" ? 0 : 1;
                                            if (sw.ContainsKey("x"))
                                                cb_xsize1.SelectedIndex = cb_xsize1.Items.IndexOf(sw["x"]);
                                            if (sw.ContainsKey("y"))
                                                nud_ysize1.Value = Convert.ToInt16(sw["y"]);
                                            if (sw.ContainsKey("h"))
                                                nud_xoffset1.Value = Convert.ToInt16(sw["h"]);
                                            if (sw.ContainsKey("v"))
                                                nud_yoffset1.Value = Convert.ToInt16(sw["v"]);
                                            break;
                                        default:
                                            break;
                                    }
                                }
                                break;
                            case "sw":
                                if (sw.ContainsKey("m"))
                                    cb_vswmode.SelectedIndex = Convert.ToInt16(sw["m"]);
                                if (sw.ContainsKey("c"))
                                    cb_vswch.SelectedIndex = Convert.ToInt16(sw["c"]);
                                if (sw.ContainsKey("l"))
                                    nud_vswmin.Value = Convert.ToDecimal(sw["l"]);
                                if (sw.ContainsKey("h"))
                                    nud_vswmax.Value = Convert.ToDecimal(sw["h"]);
                                if (sw.ContainsKey("t"))
                                    nud_vswtimer.Value = Convert.ToDecimal(sw["t"]);

                                break;
                            default:
                                break;
                        }
                        break;
                    case "mavlink":
                        switch (cmd.Split(' ')[1])
                        {
                            case "config":
                                if (sw.ContainsKey("i"))
                                    nud_osdsysid.Value = Convert.ToDecimal(sw["i"]);
                                if (sw.ContainsKey("u"))
                                    nud_uavsysid.Value = Convert.ToDecimal(sw["u"]);
                                if (sw.ContainsKey("h"))
                                    cbx_mavhb.Checked = sw["h"] == "1" ? true : false;
                                break;
                            case "rates":
                                if (sw.ContainsKey("s") && sw.ContainsKey("r"))
                                {
                                    switch (sw["s"])
                                    {
                                        case "1":
                                            nud_streamRawSensors.Value = Convert.ToDecimal(sw["r"]);
                                            break;
                                        case "2":
                                            nud_streamExtStatus.Value = Convert.ToDecimal(sw["r"]);
                                            break;
                                        case "3":
                                            nud_streamRcChannels.Value = Convert.ToDecimal(sw["r"]);
                                            break;
                                        case "4":
                                            nud_streamRawCtrl.Value = Convert.ToDecimal(sw["r"]);
                                            break;
                                        case "5":
                                            nud_streamPosition.Value = Convert.ToDecimal(sw["r"]);
                                            break;
                                        case "6":
                                            nud_streamExtra1.Value = Convert.ToDecimal(sw["r"]);
                                            break;
                                        case "7":
                                            nud_streamExtra2.Value = Convert.ToDecimal(sw["r"]);
                                            break;
                                        case "8":
                                            nud_streamExtra3.Value = Convert.ToDecimal(sw["r"]);
                                            break;
                                        default:
                                            break;
                                    }
                                }
                                break;
                            default:
                                break;
                        }
                        break;
                    case "flight":
                        switch (cmd.Split(' ')[1])
                        {
                            case "rssi":
                                if (sw.ContainsKey("s"))
                                {
                                    int src = Convert.ToInt16(sw["s"]);
                                    if (src > 17)
                                        src -= 11;
                                    cb_rssi_src.SelectedIndex = src;
                                }
                                if (sw.ContainsKey("u"))
                                    cb_rssi_units.SelectedIndex = Convert.ToInt16(sw["u"]);
                                if (sw.ContainsKey("l"))
                                    nud_rssi_min.Value = Convert.ToDecimal(sw["l"]);
                                if (sw.ContainsKey("h"))
                                    nud_rssi_max.Value = Convert.ToDecimal(sw["h"]);
                                break;
                            case "alarms":
                                if (!sw.ContainsKey("a"))
                                    break;
                                switch (sw["a"])
                                {
                                    case "3":
                                        /* remove all */
                                        lb_fa_cfg.Items.Clear();
                                        break;
                                    case "1":
                                    case "2":
                                        int faid = lb_fa_cfg.Items.Count;
                                        int id = Convert.ToInt16(sw["i"]);
                                        string fa_name = lb_fa.Items[faid].ToString();
                                        lb_fa_cfg.Items.Add("(" + faid + "/" + id + ")" + fa_name);
                                        flight_alarms.Add(faid, id + " " + sw["a"] + " " + sw["v"] + " " + sw["t"]);
                                        break;
                                    default:
                                        break;
                                }
                                break;
                            default:
                                break;
                        }
                        break;
                    case "tabs":
                        if (cmd.Split(' ')[1] != "config")
                            break;
                        if (sw.ContainsKey("m"))
                            cb_tabmode.SelectedIndex = Convert.ToInt16(sw["m"]);
                        if (sw.ContainsKey("c"))
                            cb_tabch.SelectedIndex = Convert.ToInt16(sw["c"]);
                        if (sw.ContainsKey("l"))
                            nud_tabmin.Value = Convert.ToDecimal(sw["l"]);
                        if (sw.ContainsKey("h"))
                            nud_tabmax.Value = Convert.ToDecimal(sw["h"]);
                        if (sw.ContainsKey("t"))
                            nud_tabtimer.Value = Convert.ToDecimal(sw["t"]) * 100;
                        break;
                    case "config":
                        switch (cmd.Split(' ')[1])
                        {
                            case "units":
                                cb_units.SelectedIndex = cmd.Split(' ')[2] == "0" ? 0 : 1;
                                break;
                            default:
                                break;
                        }
                        break;
                    case "widgets":
                        /* TODO */
                        break;
                    case "echo":
                        continue;
                    default:
                        txt_log.AppendText("Warning: unknown command: " + mod + "\n");
                        break;
                }
            }
        }


        private void get_config2()
        {
            send_cmd("config dump2");

            List<string> config = new List<string> { };
            string line;
            do
            {
                line = comPort.ReadLine();
                txt_log.AppendText(line + '\n');
                config.Add(line);

            } while (!line.StartsWith("# end"));



            parse_shell_config(config);

        }

        private void get_config()
        {
            send_cmd("config dump");
            Console.Write("dump config");
            List<string> config = new List<string> { };
            string line;
            bool started = false;
            bool finished = false;
            while (!finished)
            {
                Console.Write(".");
                line = comPort.ReadLine();
                if (line.Contains("AlceOSD config"))
                {
                    started = true;
                    txt_log.AppendText(line + '\n');
                }
                if (started)
                {
                    config.Add(line);
                    if (line.StartsWith("--"))
                        finished = true;
                }

            }
            Console.WriteLine(" done");
            parse_config(config.ToArray());
        }
        

        bool shell_active = false;
        bool init_done = false;

        int his_idx = 0;
        List<string> cmd_history = new List<string> { };


        void disconnect()
        {
            bt_sendTlog.Enabled = false;
            bt_sendTlog.Text = "Send .tlog";
            bt_conn.Enabled = false;
            bt_submitCfg.Enabled = false;

            timer_heartbeat.Enabled = false;

            timer_com.Enabled = false;
            shell_active = false;
            init_done = false;
            comPort.Close();

            bt_conn.Text = "Connect";
            readConfigToolStripMenuItem.Enabled = true;
            writeConfigToolStripMenuItem.Enabled = true;
            bt_flash_fw.Enabled = true;
            cbx_mavmode.Enabled = true;
            bt_conn.Enabled = true;
        }





        private void bt_conn_Click(object sender, EventArgs e)
        {
            if (shell_active)
            {
                disconnect();
            }
            else
            {
                bt_conn.Enabled = false;
                cbx_mavmode.Enabled = false;
                setup_comport();
                if (!open_comport())
                {
                    bt_conn.Enabled = true;
                    cbx_mavmode.Enabled = true;
                    return;
                }

                if (!reset_board(false, cbx_mavmode.Checked))
                {
                    MessageBox.Show("Error: no response from board", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    comPort.Close();
                    bt_conn.Enabled = true;
                    cbx_mavmode.Enabled = true;
                    return;
                }

                shell_active = true;

                System.Threading.Thread.Sleep(1000);
                comPort.DiscardInBuffer();


                get_widget_ids();
                get_flight_alarms();
                get_config();
                load_lb_widgets_canvas();

                timer_com.Enabled = true;
                init_done = true;

                send_cmd("version");

                bt_conn.Text = "Disconnect";
                readConfigToolStripMenuItem.Enabled = false;
                writeConfigToolStripMenuItem.Enabled = false;
                bt_flash_fw.Enabled = false;

                bt_submitCfg.Enabled = true;

                if (cbx_mavmode.Checked)
                {
                    timer_heartbeat.Enabled = true;
                    bt_sendTlog.Enabled = true;
                }

                bt_conn.Enabled = true;
            }
        }

        private string[] cmd_get_answer()
        {
            List<string> s = new List<string> ();
            string line;
            do
            {
                line = comPort.ReadLine();
                Console.WriteLine("line={0}", line);
                s.Add(line);
            } while (line != "> ");
            return s.ToArray();
        }

        private bool send_cmd(string cmd, bool echo = true)
        {
            bool ret = false;
            if (!comPort.IsOpen)
                return ret;

            timer_com.Enabled = false;
            comPort.DiscardInBuffer();
            comPort.WriteLine(cmd);
            try
            {

                if (cmd == "reboot")
                {
                    System.Threading.Thread.Sleep(200);
                    timer_com.Enabled = false;
                    reset_board(false, cbx_mavmode.Checked);
                    System.Threading.Thread.Sleep(200);
                    comPort.DiscardInBuffer();
                    timer_com.Enabled = true;
                    comPort.WriteLine("version");
                    ret = true;
                }
                else
                {
                    string ans = comPort.ReadLine();
                    if (init_done && echo)
                        txt_shell.AppendText(ans + "\n");
                    if (ans.Contains(cmd))
                        ret = true;
                    else
                        ret = false;
                }
            }
            catch
            {
                //MessageBox.Show("shell_cmd: no response", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ret = false;
            }
            timer_com.Enabled = true;
            return ret;
        }

        private void timer_com_Tick(object sender, EventArgs e)
        {
            if (!shell_active)
                timer_com.Enabled = false;

            if (comPort.BytesToRead < 1)
                return;

            string ans = comPort.ReadExisting();
            ans = Regex.Replace(ans, "(?<!\r)\n", "\r\n");
            txt_shell.AppendText(ans);
        }

        private void tb_cmdLine_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\t')
            {
                //e.Handled = true;
            }
        }

        private void tb_cmdLine_KeyDown(object sender, KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Tab) || (e.KeyCode == Keys.Enter))
            {
                e.SuppressKeyPress = true;
            }

            if (!comPort.IsOpen)
                return;

            if (!shell_active)
                return;

            string cmd = tb_cmdLine.Text.Trim('\n').Trim('\r');

            switch (e.KeyCode)
            {
                case Keys.Enter:
                    bool add2hist = true;
                    if (cmd == "")
                        add2hist = false;

                    if ((cmd_history.Count > 0) && (cmd == cmd_history.ElementAt(cmd_history.Count - 1)))
                        add2hist = false;

                    if (add2hist)
                    {
                        cmd_history.Add(cmd);
                        lb_history.Items.Add(cmd);
                    }

                    send_cmd(cmd);
                    tb_cmdLine.Clear();
                    e.Handled = true;
                    his_idx = 0;
                    break;
                case Keys.Up:
                    if (cmd_history.Count == 0)
                        break;
                    his_idx++;
                    if (his_idx > cmd_history.Count)
                        his_idx = cmd_history.Count;

                    lb_history.SelectedIndex = cmd_history.Count - his_idx;
                    tb_cmdLine.Text = cmd_history.ElementAt(cmd_history.Count - his_idx);
                    tb_cmdLine.SelectionStart = tb_cmdLine.Text.Length;
                    break;

                case Keys.Down:
                    his_idx--;
                    if (his_idx <= 0)
                    {
                        tb_cmdLine.Text = "";
                        his_idx = 0;
                        lb_history.SelectedIndex = -1;
                    }
                    else
                    {
                        lb_history.SelectedIndex = cmd_history.Count - his_idx;
                        tb_cmdLine.Text = cmd_history.ElementAt(cmd_history.Count - his_idx);
                        tb_cmdLine.SelectionStart = tb_cmdLine.Text.Length;
                    }
                    break;

                case Keys.Tab:
                    //comPort.Write(tb_cmdLine.Text + "\t");
                    break;

                default:
                    break;
            }


        }


        private bool get_widget_config(string name_uid)
        {
            string wid = name_uid.Substring(0, name_uid.Length - 1);
            if (!widget_ids.ContainsKey(wid))
                return false;

            timer_com.Enabled = false;

            char uid = name_uid.ElementAt(name_uid.Length - 1);
            string req = widget_ids[wid].ToString() + "+" + uid.ToString();

            send_cmd("widgets cfg -i " + req);
            System.Threading.Thread.Sleep(100);

            string line;
            do
            {
                line = comPort.ReadLine();
                if (line.Contains("Widget not found"))
                {
                    timer_com.Enabled = true;
                    return false;
                }

            } while (!line.StartsWith("t:"));


            Dictionary<string, string> maptable = new Dictionary<string, string>
            {
                {"t", "TAB" },
                {"x", "X" },
                {"y", "Y" },
                {"h", "HJUST" },
                {"v", "VJUST" },
                {"m", "MODE" },
                {"s", "SOURCE" },
                {"u", "UNITS" },
                {"a", "PARAM1" },
                {"b", "PARAM2" },
                {"c", "PARAM3" },
                {"d", "PARAM4" },
            };

            MatchCollection mc;
            mc = Regex.Matches(line, @"(.):([-]?\d+)");
            foreach (Match m in mc)
            {
                if (m.Success)
                {
                    Console.WriteLine("{0} = {1}", m.Groups[1].Value, m.Groups[2].Value); //Convert.ToInt16(g.Value));
                    string param = m.Groups[1].Value;
                    double value = Convert.ToDouble(m.Groups[2].Value);
                    widgets[name_uid][maptable[param]] = value;
                }
            }

            timer_com.Enabled = true;
            return true;
        }



        private Canvas get_widget_bitmap(string id)
        {
            Canvas c = new Canvas();
            String line;
            c.width = 0; c.height = 0;

            timer_com.Enabled = false;
            send_cmd("widgets bitmap " + id);

            do
            {
                Match m;
                line = comPort.ReadLine();

                m = Regex.Match(line, @"^w:(\d+)");
                if (m.Success)
                {
                    c.width = Convert.ToInt16(m.Groups[1].Value);
                    continue;
                }

                m = Regex.Match(line, @"^h:(\d+)");
                if (m.Success)
                {
                    c.height = Convert.ToInt16(m.Groups[1].Value);
                }

            } while (!line.StartsWith("h:"));
            int size = c.width * c.height;
            Console.WriteLine("w:{0} h:{1} s:{2}", c.width, c.height, size);
            if (size > 0)
            {
                int left = size;
                byte[] data = new byte[size];
                comPort.Read(data, 0, size);
                c.bitmap = data;
            }

            System.Threading.Thread.Sleep(100);
            comPort.DiscardInBuffer();

            timer_com.Enabled = true;
            return c;
        }

        private void pb_osd_Paint(object sender, PaintEventArgs e)
        {
            //Graphics g = pb_osd.CreateGraphics();
            //g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            //g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            //g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.None;

            IntPtr hdc = e.Graphics.GetHdc();
            int x, y, b, v, d;
            uint bgr;

            foreach (KeyValuePair<string, Canvas> _c in ca)
            {
                Canvas c = _c.Value;
                if (!c.shown)
                    continue;

                //c.bitmap.ToList().ForEach(_b => Console.Write(" {0:X}", _b));
                //Console.WriteLine("");

                //txt_log.AppendText("canvas w" + c.width + "\n");
                for (y = 0; y < c.height; y++)
                {
                    for (x = 0; x < c.width; x++)
                    {
                        if (y * c.width + x >= c.bitmap.Length)
                            continue;

                        v = c.bitmap[y * c.width + x];
                        for (b = 0; b < 4; b++)
                        {
                            d = (v >> (6 - (2 * b))) & 3;
                            bgr = 0;
                            switch (d)
                            {
                                case 1:
                                    bgr = 0xffffff;
                                    break;
                                case 2:
                                    bgr = 0x808080;
                                    break;
                                case 3:
                                    bgr = 0x000000;
                                    break;
                                default:
                                    break;
                            }
                            if (d > 0)
                                GDI.SetPixel(hdc, c.x0 + x * 4 + b, c.y0 + y, bgr);
                        }
                    }
                }
            }

            e.Graphics.ReleaseHdc(hdc);

            string name_uid = get_selected_widget();
            if (ca.ContainsKey(name_uid))
            {
                Canvas c = ca[name_uid];
                Rectangle ee = new Rectangle(c.x0, c.y0, c.width * 4, c.height);
                using (Pen pen = new Pen(Color.Red, 2)) { e.Graphics.DrawRectangle(pen, ee); }
            }
        }

        private Canvas get_widget_canvas(string name_uid)
        {
            string iduid = get_widget_iduid(name_uid);
            Console.WriteLine("get_widget_canvas() iduid={0} {1}", iduid, name_uid);
            Canvas c = get_widget_bitmap(iduid);
            if (c.width > 0)
            {
                c.x0 = pos2canvas(name_uid, c, 'H');
                c.y0 = pos2canvas(name_uid, c, 'V');
            }
            return c;
        }

        bool load_lock = false;
        private void load_lb_widgets_canvas()
        {
            if (!shell_active)
                return;

            if (load_lock)
                return;
            load_lock = true;

            recalculate_pb_osd_size();

            int tab = (int) nud_seltab.Value;

            send_cmd("tabs load -t " + tab);
            System.Threading.Thread.Sleep(200);
            comPort.DiscardInBuffer();

            string[] keys = ca.Keys.ToArray();
            foreach (string name_uid in keys)
            {
                Canvas c = ca[name_uid];
                c.x0 = pos2canvas(name_uid, c, 'H');
                c.y0 = pos2canvas(name_uid, c, 'V');
                c.shown = lb_widgets.Items.Contains(name_uid);
                ca[name_uid] = c;
            }

            string[] lst = new string[lb_widgets.Items.Count];
            lb_widgets.Items.CopyTo(lst, 0);

            int total = 0, size = lst.Length;
            foreach (string name_uid in lst)
            {
                if (!ca.ContainsKey(name_uid))
                {
                    Canvas c = get_widget_canvas(name_uid);
                    if (c.width > 0)
                    {
                        c.shown = true;
                        ca[name_uid] = c;
                    }
                }

                total++;
                int progress = (total * 100) / size;
                if (progress != pb.Value)
                {
                    pb.Value = progress;
                    Application.DoEvents();
                }

            }
            pb_osd.Invalidate();
            load_lock = false;
        }


        Point _startPt = new Point();
        bool _tracking = false;
        decimal nud_x0, nud_y0;

        private void pb_osd_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                string name_uid = get_selected_widget();
                _startPt = e.Location;
                if (ca.ContainsKey(name_uid))
                {
                    Canvas c = ca[name_uid];
                    if ( (_startPt.X >= c.x0) && (_startPt.X <= (c.x0 + c.width*4)) &&
                         (_startPt.Y >= c.y0) && (_startPt.Y <= (c.y0 + c.height)))
                    {
                        _tracking = true;
                    }
                }

                if (! _tracking)
                {
                    foreach(KeyValuePair<String,Canvas> pair in ca)
                    {
                        Canvas c = pair.Value;
                        if (!c.shown)
                            continue;

                        if ((_startPt.X >= c.x0) && (_startPt.X <= (c.x0 + c.width * 4)) &&
                            (_startPt.Y >= c.y0) && (_startPt.Y <= (c.y0 + c.height)))
                        {
                            lb_widgets.SelectedItem = pair.Key;
                            _tracking = true;
                            pb_osd.Invalidate();
                            break;
                        }
                    }
                }

                if (_tracking)
                {
                    nud_x0 = nud_wxpos.Value;
                    nud_y0 = nud_wypos.Value;
                }
            }
        }

        private void pb_osd_MouseMove(object sender, MouseEventArgs e)
        {
            if (_tracking)
            {
                int dx = (e.X - _startPt.X);
                int dy = (e.Y - _startPt.Y);
                nud_wxpos.Value = (int) (nud_x0 + dx) & ~3;
                nud_wypos.Value = nud_y0 + dy;
            }
        }

        private void pb_osd_MouseUp(object sender, MouseEventArgs e)
        {
            if (_tracking)
                _tracking = false;
        }

        private void lb_history_DoubleClick(object sender, EventArgs e)
        {
            if (lb_history.SelectedIndex < 0)
                return;

            string cmd = lb_history.SelectedItem.ToString();
            send_cmd(cmd);
        }

        private void bt_submitCfg_Click(object sender, EventArgs e)
        {
            send_cmd("config save");
        }

        private void txt_shell_VisibleChanged(object sender, EventArgs e)
        {
            if (txt_shell.Visible)
            {
                txt_shell.SelectionStart = txt_shell.Text.Length;
                txt_shell.ScrollToCaret();
            }
        }

        private void timer_heartbeat_Tick(object sender, EventArgs e)
        {
            MavlinkPacket p = new MavlinkPacket();
            Msg_heartbeat hb = new Msg_heartbeat();

            hb.autopilot = (byte) MAV_AUTOPILOT.MAV_AUTOPILOT_INVALID;
            hb.type = (byte)MAV_TYPE.MAV_TYPE_GCS;

            p.Message = hb;
            p.SystemId = 201;
            p.ComponentId = 0;

            byte[] packet = comPort.mav.Send(p);
            if (comPort.mav_packet_send(packet))
            {
                timer_heartbeat.Enabled = false;
                disconnect();
                MessageBox.Show("Error writing to COM port! Disconnecting...", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            tb_tlog.Value = comPort.progress;

        }

        private void bt_refreshCanvas_Click(object sender, EventArgs e)
        {
            if (load_lock)
                return;
            load_lock = true;

            string[] lst = new string[lb_widgets.Items.Count];
            lb_widgets.Items.CopyTo(lst, 0);

            int total = 0, size = lst.Length;
            pb.Value = 0;
            foreach (string name_uid in lst)
            {
                Canvas c;
                try
                {
                    c = get_widget_canvas(name_uid);
                    if (c.width > 0)
                    {
                        c.shown = true;
                        ca[name_uid] = c;
                    }
                }
                catch
                {
                    MessageBox.Show("Error loading bitmap for " + name_uid, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                total++;
                int progress = (total * 100) / size;
                if (progress != pb.Value)
                {
                    pb.Value = progress;
                    Application.DoEvents();
                }

            }
            pb_osd.Invalidate();
            load_lock = false;
        }

        /* ********************************************************************************* */
        /* ********************************************************************************* */
        /* video config update callback stuff */

        private void update_video_config()
        {
            string cmd = "video config ";

            cmd += " -w" + nud_whitelvl.Value;
            cmd += " -g" + nud_graylvl.Value;
            cmd += " -b" + nud_blacklvl.Value;
            cmd += " -r" + nud_vsync0.Value;
            cmd += " -f" + nud_vsync1.Value;
            send_cmd(cmd);
        }

        private void update_video_config0()
        {
            string cmd = "video config -p0";
            switch (cb_vidscan.SelectedIndex)
            {
                default:
                case 0:
                    cmd += " -s p -m p";
                    break;
                case 1:
                    cmd += " -s p -m i";
                    break;
                case 2:
                    cmd += " -s n -m p";
                    break;
                case 3:
                    cmd += " -s n -m i";
                    break;
            }
            cmd += " -x" + cb_xsize.Text;
            cmd += " -y" + nud_ysize.Value;
            cmd += " -h" + nud_xoffset.Value;
            cmd += " -v" + nud_yoffset.Value;
            send_cmd(cmd);
        }

        private void update_video_config1()
        {
            string cmd = "video config -p1";
            switch (cb_vidscan1.SelectedIndex)
            {
                default:
                case 0:
                    cmd += " -s p -m p";
                    break;
                case 1:
                    cmd += " -s p -m i";
                    break;
                case 2:
                    cmd += " -s n -m p";
                    break;
                case 3:
                    cmd += " -s n -m i";
                    break;
            }
            cmd += " -x" + cb_xsize1.Text;
            cmd += " -y" + nud_ysize1.Value;
            cmd += " -h" + nud_xoffset1.Value;
            cmd += " -v" + nud_yoffset1.Value;
            send_cmd(cmd);
        }

        private void update_video_sw_config()
        {
            string cmd = "video sw -m" + cb_vswmode.SelectedIndex;
            cmd += " -c" + (cb_vswch.SelectedIndex + 1);
            cmd += " -l" + nud_vswmin.Value;
            cmd += " -h" + nud_vswmax.Value;
            cmd += " -t" + nud_vswtimer.Value;
            send_cmd(cmd);
        }

        private void video_config_changed(object sender, EventArgs e)
        {
            timer_submit_cfg_tag("video");
        }

        private void video0_config_changed(object sender, EventArgs e)
        {
            timer_submit_cfg_tag("video0");
        }

        private void video1_config_changed(object sender, EventArgs e)
        {
            timer_submit_cfg_tag("video1");
        }

        private void videosw_config_changed(object sender, EventArgs e)
        {
            timer_submit_cfg_tag("videosw");
        }

        /* ********************************************************************************* */
        /* ********************************************************************************* */
        /* uart config update callback stuff */

        private void update_uart1_config()
        {
            string cmd = "uart config -p0";
            string baudrate = cb_baud1.Text;
            string mode = cb_mode1.Text.ToLower();
            cmd += " -b" + baudrate;
            cmd += " -c" + mode;
            send_cmd(cmd);
        }
        private void update_uart2_config()
        {
            string cmd = "uart config -p1";
            string baudrate = cb_baud2.Text;
            string mode = cb_mode2.Text.ToLower();
            cmd += " -b" + baudrate;
            cmd += " -c" + mode;
            send_cmd(cmd);
        }
        private void update_uart3_config()
        {
            string cmd = "uart config -p2";
            string baudrate = cb_baud3.Text;
            string mode = cb_mode3.Text.ToLower();
            cmd += " -b" + baudrate;
            cmd += " -c" + mode;
            send_cmd(cmd);
        }
        private void update_uart4_config()
        {
            string cmd = "uart config -p3";
            string baudrate = cb_baud4.Text;
            string mode = cb_mode4.Text.ToLower();
            cmd += " -b" + baudrate;
            cmd += " -c" + mode;
            send_cmd(cmd);
        }

        private void uart1_config_changed(object sender, EventArgs e)
        {
            timer_submit_cfg_tag("uart1");
        }

        private void uart2_config_changed(object sender, EventArgs e)
        {
            timer_submit_cfg_tag("uart2");
        }

        private void uart3_config_changed(object sender, EventArgs e)
        {
            timer_submit_cfg_tag("uart3");
        }

        private void uart4_config_changed(object sender, EventArgs e)
        {
            timer_submit_cfg_tag("uart4");
        }

        /* ********************************************************************************* */
        /* ********************************************************************************* */
        /* misc config update callback stuff */

        private void update_tabs_config()
        {
            int mode = cb_tabmode.SelectedIndex;
            int timer = (int)nud_tabtimer.Value;
            int ch = cb_tabch.SelectedIndex;
            int chmin = (int)nud_tabmin.Value;
            int chmax = (int)nud_tabmax.Value;
            string cmd = "tabs config -m" + mode;
            cmd += " -c" + ch;
            cmd += " -l" + chmin;
            cmd += " -h" + chmax;
            cmd += " -t" + timer;
            send_cmd(cmd);
        }

        private void update_rssi_config()
        {
            int src = cb_rssi_src.SelectedIndex;
            if (src > 17)
                src += 11;
            int units = cb_rssi_units.SelectedIndex;
            int min = (int)nud_rssi_min.Value;
            int max = (int)nud_rssi_max.Value;
            string cmd = "flight rssi -s" + src;
            cmd += " -u" + units;
            cmd += " -l" + min;
            cmd += " -h" + max;
            send_cmd(cmd);
        }

        private void tabs_config_changed(object sender, EventArgs e)
        {
            timer_submit_cfg_tag("tabs");
        }

        private void rssi_config_changed(object sender, EventArgs e)
        {
            timer_submit_cfg_tag("rssi");
        }

        /* ********************************************************************************* */
        /* ********************************************************************************* */
        /* mavlink callback stuff */

        private void update_mavlink_stream_rates(int stream_id, decimal rate)
        {
            if (!init_done)
                return;
            string cmd = "mavlink rates -s" + stream_id;
            cmd += " -r" + rate;
            send_cmd(cmd);
        }

        private void nud_streamRawSensors_ValueChanged(object sender, EventArgs e)
        {
            update_mavlink_stream_rates(1, nud_streamRawSensors.Value);
        }

        private void nud_streamExtStatus_ValueChanged(object sender, EventArgs e)
        {
            update_mavlink_stream_rates(2, nud_streamExtStatus.Value);
        }

        private void nud_streamRcChannels_ValueChanged(object sender, EventArgs e)
        {
            update_mavlink_stream_rates(3, nud_streamRcChannels.Value);
        }

        private void nud_streamRawCtrl_ValueChanged(object sender, EventArgs e)
        {
            update_mavlink_stream_rates(4, nud_streamRawCtrl.Value);
        }

        private void nud_streamPosition_ValueChanged(object sender, EventArgs e)
        {
            update_mavlink_stream_rates(5, nud_streamPosition.Value);
        }

        private void nud_streamExtra1_ValueChanged(object sender, EventArgs e)
        {
            update_mavlink_stream_rates(6, nud_streamExtra1.Value);
        }

        private void nud_streamExtra2_ValueChanged(object sender, EventArgs e)
        {
            update_mavlink_stream_rates(7, nud_streamExtra2.Value);
        }

        private void nud_streamExtra3_ValueChanged(object sender, EventArgs e)
        {
            update_mavlink_stream_rates(8, nud_streamExtra3.Value);
        }

        private void update_mavlink_config()
        {
            string cmd = "mavlink config -i" + nud_osdsysid.Value;
            cmd += " -u" + nud_uavsysid.Value;
            cmd += " -h" + (cbx_mavhb.Checked ? "1" : "0");
            send_cmd(cmd);
        }

        private void mavlink_config_changed(object sender, EventArgs e)
        {
            timer_submit_cfg_tag("mavlink");
        }

        /* ********************************************************************************* */
        /* ********************************************************************************* */
        /* alarms update callback stuff */

        Dictionary<int, string> flight_alarms = new Dictionary<int, string>();
        private void bt_fa_add_Click(object sender, EventArgs e)
        {
            int id = lb_fa.SelectedIndex;

            //Console.WriteLine("selected alarm: {0}", id);

            if (id < 0)
                return;

            int l = cb_fa_type.SelectedIndex == 0 ? 1 : 2;
            int timer = (int)nud_fa_timer.Value / 100;
            double value = 0;
            try
            {
                value = Convert.ToDouble(tb_fa_value.Text);
            }
            catch
            {
                tb_fa_value.Text = "0";
                value = 0;
            }

            send_cmd("flight alarms -a" + l + " -i" + id + " -v" + value + " -t" + timer);
            get_flight_alarms();
        }

        private int get_faid()
        {
            if (lb_fa_cfg.SelectedIndex == -1)
                return -1;

            string fa_name = lb_fa_cfg.Items[lb_fa_cfg.SelectedIndex].ToString();
            Match m = Regex.Match(fa_name, @"\((.+)\/(.+)\).+");
            if (!m.Success)
            {
                return -1;
            }

            int faid = Convert.ToInt16(m.Groups[1].Value);
            return faid;
        }

        private void bt_fa_del_Click(object sender, EventArgs e)
        {
            int faid = get_faid();
            send_cmd("flight alarms -a0 -n" + faid);
            get_flight_alarms();
        }

        private void bt_fa_savealarm_Click(object sender, EventArgs e)
        {
            int sel = lb_fa_cfg.SelectedIndex;
            int faid = get_faid();
            int l = cb_fa_type.SelectedIndex == 0 ? 1 : 2;
            int timer = (int)nud_fa_timer.Value / 100;
            double value = 0;
            try
            {
                value = Convert.ToDouble(tb_fa_value.Text);
            }
            catch
            {

            }
            send_cmd("flight alarms -a" + l + " -n" + faid + " -v" + value + " -t" + timer);
            get_flight_alarms();
            lb_fa_cfg.SelectedIndex = sel;
        }

        private void lb_fa_cfg_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lb_fa_cfg.SelectedIndex == -1)
            {
                return;
            }
            bt_fa_savealarm.Enabled = true;

            string fa_name = lb_fa_cfg.Items[lb_fa_cfg.SelectedIndex].ToString();
            Match m = Regex.Match(fa_name, @"\((.+)\/(.+)\).+");
            if (!m.Success)
            {
                return;
            }

            int faid = Convert.ToInt16(m.Groups[1].Value);
            int id = Convert.ToInt16(m.Groups[2].Value);
            string[] cfg = flight_alarms[faid].Split(' ');

            lbl_fa_name.Text = lb_fa.Items[id].ToString();

            cb_fa_type.SelectedIndex = cfg[1] == "1" ? 0 : 1;
            nud_fa_timer.Value = Convert.ToInt16(cfg[3]) * 100;
            tb_fa_value.Text = cfg[2];
        }

        //Dictionary<string, int> flight_alarms_ids = new Dictionary<string, int>();
        private void get_flight_alarms()
        {
            int state = 0;
            bool done = false;

            send_cmd("flight alarms", false);

            flight_alarms.Clear();
            lb_fa_cfg.Items.Clear();
            while (!done)
            {
                string line = comPort.ReadLine();

                switch (state)
                {
                    case 0:
                    default:
                        /* start */
                        if (line.StartsWith("Available alarms"))
                            state = 1;
                        break;
                    case 1:
                        /* get available alarms */
                        if (line.Trim() == "")
                        {
                            state = 2;
                            break;
                        }
                        //string[] info = line.Split(',');
                        //widget_ids.Add(info[2].Trim(), Convert.ToInt16(info[0]));

                        //Console.WriteLine("getflightalarmsids: {0}", line);
                        break;
                    case 2:
                        if (line.StartsWith("Active alarms"))
                            state = 3;
                        break;
                    case 3:
                        /* get configure alarms */
                        if (line.Trim() == "")
                        {
                            state = 4;
                            break;
                        }

                        int faid, id, mode, timer;
                        double value;
                        Match m = Regex.Match(line, @"nr(.+)\s\((.+)\).*mode=(.).*val=(.+)\stimer=(.+)\)");
                        if (m.Success)
                        {
                            faid = Convert.ToInt16(m.Groups[1].Value);
                            id = Convert.ToInt16(m.Groups[2].Value);
                            mode = Convert.ToInt16(m.Groups[3].Value);
                            value = Convert.ToDouble(m.Groups[4].Value, CultureInfo.InvariantCulture.NumberFormat);
                            timer = Convert.ToInt16(m.Groups[5].Value);

                            flight_alarms.Add(faid, id + " " + mode + " " + value + " " + timer);

                            string fa_name = lb_fa.Items[id].ToString();

                            lb_fa_cfg.Items.Add("(" + faid + "/" + id + ")" + fa_name);

                            Console.WriteLine("configured flight alarms: {0} {1} {2} {3}", id, mode, value, timer);
                        }
                        break;
                    case 4:
                        /* done */
                        if (line.Contains("remove all"))
                            done = true;
                        break;
                }
            }
            bt_fa_savealarm.Enabled = false;
        }

        /* ********************************************************************************* */
        /* ********************************************************************************* */
        /* update callback stuff */

        private void timer_submit_cfg_tag(string tag)
        {
            timer_submit_cfg.Stop();

            List<string> cfg = (List<string>)timer_submit_cfg.Tag;
            if (cfg == null)
                cfg = new List<string>();

            if (!cfg.Contains(tag))
                cfg.Add(tag);

            timer_submit_cfg.Tag = cfg;
            timer_submit_cfg.Start();
        }

        private void timer_submit_cfg_Tick(object sender, EventArgs e)
        {
            List<string> cfg = (List<string>)timer_submit_cfg.Tag;
            if (cfg == null)
                return;

            timer_submit_cfg.Stop();

            if (!init_done)
            {
                timer_submit_cfg.Tag = null;
                return;
            }

            foreach (string c in cfg)
            {
                switch (c)
                {
                    case "video":
                        update_video_config();
                        break;
                    case "video0":
                        update_video_config0();
                        break;
                    case "video1":
                        update_video_config1();
                        break;
                    case "videosw":
                        update_video_sw_config();
                        break;

                    case "uart1":
                        update_uart1_config();
                        break;
                    case "uart2":
                        update_uart2_config();
                        break;
                    case "uart3":
                        update_uart3_config();
                        break;
                    case "uart4":
                        update_uart4_config();
                        break;

                    case "tabs":
                        update_tabs_config();
                        break;
                    case "rssi":
                        update_rssi_config();
                        break;

                    case "mavlink":
                        update_mavlink_config();
                        break;
                    default:
                        break;
                }
            }
            timer_submit_cfg.Tag = null;
        }

        /* ********************************************************************************* */
        /* ********************************************************************************* */
        /* ... stuff */


        private void button1_Click(object sender, EventArgs e)
        {
            get_config2();
        }

        private void importOldConfigToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string[] config;
            ofd_loadcfg.FileName = def_filename;
            if (ofd_loadcfg.ShowDialog() == DialogResult.OK)
            {
                def_filename = ofd_loadcfg.FileName;
                file_opened = true;

                this.Text = "AlceOSD Config Editor - " + Path.GetFileName(def_filename);

                using (StreamReader sr = new StreamReader(ofd_loadcfg.OpenFile()))
                {
                    string fw = "";
                    fw = sr.ReadToEnd();
                    config = fw.Split('\n');
                }
                parse_config(config);
            }
        }


        private void bt_reboot_Click(object sender, EventArgs e)
        {
            send_cmd("reboot");
        }

        private void cb_units_SelectedIndexChanged(object sender, EventArgs e)
        {
            string cmd = "config units " + cb_units.SelectedIndex;
            send_cmd(cmd);
        }

        bool tlog_active = false;
        private void bt_sendTlog_Click(object sender, EventArgs e)
        {
            if (tlog_active)
            {
                bt_sendTlog.Enabled = false;


                comPort.stop_tlog();
                bt_sendTlog.Text = "Send .tlog";
                tlog_active = false;
                bt_sendTlog.Enabled = true;
            }
            else
            {
                bt_sendTlog.Enabled = false;
                DialogResult result = ofd_tlog.ShowDialog();
                if ((result != DialogResult.Cancel) && (ofd_tlog.FileName != ""))
                {
                    Console.WriteLine("sending .tlog '{0}'", ofd_tlog.FileName);
                    txt_log.AppendText("Sending telemtry log file: " + ofd_tlog.FileName + "\n");
                    byte[] bytes = System.IO.File.ReadAllBytes(ofd_tlog.FileName);
                    comPort.send_tlog(bytes);
                    bt_sendTlog.Text = "Abort .tlog";
                    tlog_active = true;
                }
                bt_sendTlog.Enabled = true;
            }

        }

        private void tb_tlog_Scroll(object sender, EventArgs e)
        {
            comPort.seek = tb_tlog.Value;
        }


        void refresh_selected_widget()
        {
            if (lb_widgets.SelectedIndex == -1)
                return;
            string name_uid = lb_widgets.Text;
            Canvas c = get_widget_canvas(name_uid);
            if (c.width > 0)
            {
                c.shown = true;
                ca[name_uid] = c;
            }
            pb_osd.Invalidate();
        }

        private void pb_osd_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            refresh_selected_widget();
        }

        private void lb_widgets_DoubleClick(object sender, EventArgs e)
        {
            refresh_selected_widget();
        }


        private void bt_upCfg_Click(object sender, EventArgs e)
        {
            if (!init_done)
                return;

            DialogResult result = ofd_upCfg.ShowDialog();
            if ((result != DialogResult.Cancel) && (ofd_upCfg.FileName != ""))
            {
                Console.WriteLine("sending config '{0}'", ofd_upCfg.FileName);
                txt_log.AppendText("Sending config file: " + ofd_upCfg.FileName + "\n");

                string[] config;
                using (StreamReader sr = new StreamReader(ofd_upCfg.OpenFile()))
                {
                    string cfg = "";
                    cfg = sr.ReadToEnd();
                    config = cfg.Split('\n');
                }

                string header;
                try
                {
                    foreach (string value in config)
                    {
                        string line = value.Trim();

                        if (line.Contains(" AlceOSD config "))
                        {
                            header = line;
                            continue;
                        }

                        if (line.StartsWith("#"))
                            continue;

                        comPort.WriteLine(line);
                        //System.Threading.Thread.Sleep(50);
                        Application.DoEvents();
                    }
                    MessageBox.Show("Config successfully uploaded", "Config", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch
                {
                    MessageBox.Show("Error uploading config", "Config", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void bt_dnCfg_Click(object sender, EventArgs e)
        {
            if (!init_done)
                return;

            DialogResult result = ofd_dnCfg.ShowDialog();
            if ((result != DialogResult.Cancel) && (ofd_dnCfg.FileName != ""))
            {
                try
                {
                    timer_com.Enabled = false;
                    send_cmd("config dump2");
                    //comPort.WriteLine("config dump2");
                    System.Threading.Thread.Sleep(100);

                    List<string> cfg = new List<string>();
                    while (true)
                    {
                        string line = comPort.ReadLine();
                        Console.WriteLine("config dump() {0}", line);
                        if (line.Contains("# end"))
                            break;
                        //Application.DoEvents();
                        cfg.Add(line);
                    }
                    timer_com.Enabled = true;

                    using (StreamWriter sw = new StreamWriter(ofd_dnCfg.FileName))
                    {
                        foreach (string element in cfg)
                            sw.WriteLine(element);
                    }
                    MessageBox.Show("Config successfully downloaded", "Config", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch
                {
                    MessageBox.Show("Error downloading config", "Config", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void cb_hwrev_SelectedIndexChanged(object sender, EventArgs e)
        {
            validate_hw_options();
        }

        private void bt_flash_fw_Click(object sender, EventArgs e)
        {
            string version = "";

            tabControl1.SelectTab(tabControl1.TabPages.IndexOfKey("tab_log"));

            setup_comport();
            if (!open_comport())
                return;
            if (!reset_board(true, false))
            {
                MessageBox.Show("Error waiting for bootloader", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }


            bool ready = false;
            string ans = "";
            string ans2;
            int timeout = 0;

            while (!ready)
            {
                while (comPort.BytesToRead < 1)
                {
                    System.Threading.Thread.Sleep(10);
                    if (++timeout > 100)
                    {
                        MessageBox.Show("Error waiting for bootloader", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        comPort.Close();
                        return;
                    }
                }

                ans += comPort.ReadExisting();

                if (ans.EndsWith("..."))
                    ready = true;

                ans2 = ans.TrimEnd();
                if (ans2.EndsWith("BIN"))
                    ready = true;
                if (ans2.EndsWith("IHEX"))
                    ready = true;
            }

            int v0 = ans.IndexOf('v') + 1;
            version = ans.Substring(v0, 3);

            txt_log.AppendText("AlceOSD bootloader version " + version + "\n");

            System.Threading.Thread.Sleep(500);
            comPort.DiscardInBuffer();

            DialogResult result = ofd_fwfile.ShowDialog();
            if ((result != DialogResult.Cancel) && (ofd_fwfile.FileName != ""))
            {
                txt_log.AppendText("Will flash file " + ofd_fwfile.FileName + "\n");
                do_flash(version);
            }
            else
            {
                byte[] abort = new byte[] { 0xff, 0xff, 0xff };
                txt_log.AppendText("Exiting bootloader...\n");
                comPort.Write(abort, 0, 3);
            }
            comPort.Close();
        }

        private void timer_submit_Tick(object sender, EventArgs e)
        {
            timer_submit.Stop();
            string name_uid = get_selected_widget();
            if (timer_submit.Tag.ToString().Contains(("P")))
                submit_widget_config_pos(name_uid);
            if (timer_submit.Tag.ToString().Contains(("O")))
                submit_widget_config_other(name_uid);
            timer_submit.Tag = "";
        }

        private void tabControl1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (tabControl1.SelectedIndex == tabControl1.TabPages.IndexOfKey("tab_widgets"))
                recalculate_pb_osd_size();
        }

    }
}
